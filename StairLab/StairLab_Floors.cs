// Coding Level: Standard
// ==========================================================================
// StairLab Floors
// Demo helper: creates a floor at every level in a StairLab JSON export,
// extending a set distance around the stair shaft.
//
// Companion to StairLabImporter and StairEnvelope. Run all three with the
// same JSON and the same insertion point. A typical demo flow:
//   1. StairLabImporter   (stairs, landings, railings, levels)
//   2. StairLabFloors     (this script: slabs at every level)
//   3. StairEnvelope      (shaft walls; the shaft opening also works,
//                          though these floors already carry the hole)
//
// Geometry:
//   - The floor outer edge offsets from the union envelope rectangle,
//     the largest extent of the shaft, so non-uniform shafts (area of
//     refuge bump-outs) measure from their widest part. Older exports
//     without an envelope fall back to the opening rectangle.
//   - Each floor carries the stair opening as an interior hole, except
//     the base level, which stays solid because the stair starts on it.
//     Openings are matched to levels by name from the JSON.
// ==========================================================================

using System.IO;
using Newtonsoft.Json.Linq;

string TransactionName = "Create StairLab Floors";

// ---------- Form 1: file and options ----------
string fileLabel = "StairLab JSON file:";
string placementLabel = "Placement:";
string floorTypeLabel = "Floor type:";
string offsetLabel = "Floor extent beyond the shaft (ft):";
List<string> placementOptions = new List<string> { "Pick insertion point", "Project origin" };

List<FloorType> floorTypes = new FilteredElementCollector(doc)
    .OfClass(typeof(FloorType))
    .Cast<FloorType>()
    .OrderBy(t => t.Name)
    .ToList();

if (!floorTypes.Any())
{
    TaskDialog.Show("No Floor Types", "The document contains no floor types.");
    return;
}

var formResults = UI.CreateCustomForm("StairLab Floors", 540, 550, form =>
{
    form.AddHeader("Create Floors Around the Stair Shaft", 18);
    form.AddLabel("Builds a floor at every level in a StairLab export, with the stair opening cut in. Use the same JSON and insertion point as the stair import.", 12, isItalic: true);
    form.AddFileSelector(fileLabel, "StairLab JSON (*.json)|*.json|All files (*.*)|*.*");
    form.AddRadioButtons(placementLabel, placementOptions, "Pick insertion point");
    form.AddComboBox(floorTypeLabel, floorTypes, t => t.Name);
    form.AddTextInput(offsetLabel, "50");
    form.AddLabel("The offset measures from the largest extent of the shaft envelope, so stepped shafts (area of refuge levels) measure from their widest part.", 11, isItalic: true);
});

if (!formResults.Success) return;

string jsonPath = formResults.GetStringResult(fileLabel);
string placementChoice = formResults.GetStringResult(placementLabel);
FloorType selectedFloorType = formResults.GetElementResult(floorTypeLabel) as FloorType;

double floorOffset = 50.0;
string offsetText = formResults.GetStringResult(offsetLabel);
double parsedOffset;
if (!string.IsNullOrEmpty(offsetText) && double.TryParse(offsetText, out parsedOffset) && parsedOffset > 0)
{
    floorOffset = parsedOffset;
}

if (selectedFloorType == null)
{
    TaskDialog.Show("Type Error", "No floor type was selected.");
    return;
}

if (string.IsNullOrEmpty(jsonPath) || !File.Exists(jsonPath))
{
    TaskDialog.Show("File Error", "Select a valid StairLab JSON file.");
    return;
}

// ---------- Parse and validate the JSON ----------
JObject design = null;
try
{
    design = JObject.Parse(File.ReadAllText(jsonPath));
}
catch (Exception ex)
{
    TaskDialog.Show("Parse Error", "Could not parse the JSON file:\n" + ex.Message);
    return;
}

JArray jsonLevels = design["levels"] as JArray;
JArray jsonOpenings = design["openings"] as JArray;
if (jsonLevels == null || jsonLevels.Count == 0)
{
    TaskDialog.Show("Invalid File", "The file does not contain StairLab levels data.");
    return;
}

string jsonUnits = (string)(design["metadata"]?["units"]) ?? "feet";
if (jsonUnits != "feet")
{
    TaskDialog.Show("Units Error", "This script expects coordinates in feet. The file declares: " + jsonUnits);
    return;
}

List<string> warnings = new List<string>();

// The floor outer boundary measures from the union envelope, the largest
// extent of the shaft. Older exports fall back to the first opening
double shaftX0 = 0, shaftY0 = 0, shaftX1 = 0, shaftY1 = 0;
JToken jsonEnvelope = design["envelope"];
if (jsonEnvelope != null)
{
    JArray envelopeOrigin = jsonEnvelope["origin"] as JArray;
    if (envelopeOrigin != null && envelopeOrigin.Count >= 2)
    {
        shaftX0 = (double)envelopeOrigin[0];
        shaftY0 = (double)envelopeOrigin[1];
    }
    shaftX1 = shaftX0 + GetDouble(jsonEnvelope, "width", 0);
    shaftY1 = shaftY0 + GetDouble(jsonEnvelope, "length", 0);
}
else if (jsonOpenings != null && jsonOpenings.Count > 0)
{
    shaftX1 = GetDouble(jsonOpenings[0], "width", 0);
    shaftY1 = GetDouble(jsonOpenings[0], "length", 0);
    warnings.Add("No envelope in the file; the offset measures from the stair opening.");
}

if (shaftX1 - shaftX0 < 0.5 || shaftY1 - shaftY0 < 0.5)
{
    TaskDialog.Show("Invalid Shaft", "The shaft dimensions in the file are not usable.");
    return;
}

// ---------- Placement origin ----------
double originX = 0, originY = 0;
if (placementChoice == "Pick insertion point")
{
    try
    {
        XYZ picked = uidoc.Selection.PickPoint("Pick the stair opening corner (same point as the stair import)");
        originX = picked.X;
        originY = picked.Y;
    }
    catch (Exception)
    {
        Console.WriteLine("Point selection cancelled. Floors aborted.");
        return;
    }
}

// ---------- Resolve every level by elevation ----------
List<Level> existingLevels = new FilteredElementCollector(doc)
    .OfClass(typeof(Level))
    .Cast<Level>()
    .ToList();

// ---------- Create the floors ----------
List<string> resultLines = new List<string>();
int floorsCreated = 0;
double baseElevation = jsonLevels.Min(l => GetDouble(l, "elevation", 0));

UI.StartProgressBar(jsonLevels.Count, "floors", showCancelButton: true);

for (int j = 0; j < jsonLevels.Count; j++)
{
    if (UI.IsProgressCancelled()) break;

    string levelName = (string)jsonLevels[j]["name"];
    double elevation = GetDouble(jsonLevels[j], "elevation", 0);
    UI.UpdateProgressBar(j, "Floor at " + levelName);

    Level hostLevel = existingLevels.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.005);
    if (hostLevel == null)
    {
        warnings.Add(levelName + ": no project level at elevation " + elevation.ToString("F2") +
            " ft. Run the StairLab Importer first. Floor skipped.");
        continue;
    }

    // Outer boundary: shaft envelope plus the offset on all sides
    List<CurveLoop> profile = new List<CurveLoop>();
    profile.Add(BuildRectLoop(
        originX + shaftX0 - floorOffset, originY + shaftY0 - floorOffset,
        originX + shaftX1 + floorOffset, originY + shaftY1 + floorOffset,
        elevation));

    // Interior hole: the stair opening at this level. The base level has
    // no opening entry, so it stays solid, matching the designer's slabs
    bool hasHole = false;
    if (jsonOpenings != null && Math.Abs(elevation - baseElevation) > 0.005)
    {
        JToken levelOpening = jsonOpenings.FirstOrDefault(o => (string)o["level"] == levelName);
        if (levelOpening != null)
        {
            JArray openingOrigin = levelOpening["origin"] as JArray;
            double ox = openingOrigin != null && openingOrigin.Count >= 2 ? (double)openingOrigin[0] : 0;
            double oy = openingOrigin != null && openingOrigin.Count >= 2 ? (double)openingOrigin[1] : 0;
            double ow = GetDouble(levelOpening, "width", 0);
            double ol = GetDouble(levelOpening, "length", 0);
            if (ow > 0.1 && ol > 0.1)
            {
                profile.Add(BuildRectLoop(
                    originX + ox, originY + oy,
                    originX + ox + ow, originY + oy + ol,
                    elevation));
                hasHole = true;
            }
        }
    }

    try
    {
        Floor newFloor = Floor.Create(doc, profile, selectedFloorType.Id, hostLevel.Id);
        floorsCreated++;
        resultLines.Add(levelName + ": floor created (Id " + newFloor.Id + ")" + (hasHole ? " with the stair opening cut" : ", solid"));
    }
    catch (Exception fex)
    {
        if (hasHole)
        {
            // Retry solid so the demo still has a floor; the shaft opening
            // from StairEnvelope can cut it instead
            try
            {
                Floor solidFloor = Floor.Create(doc, new List<CurveLoop> { profile[0] }, selectedFloorType.Id, hostLevel.Id);
                floorsCreated++;
                resultLines.Add(levelName + ": floor created solid (Id " + solidFloor.Id + "); the opening cut failed: " + fex.Message);
                warnings.Add(levelName + ": run StairEnvelope to cut the shaft through this floor.");
            }
            catch (Exception fex2)
            {
                warnings.Add(levelName + ": floor failed. " + fex2.Message);
            }
        }
        else
        {
            warnings.Add(levelName + ": floor failed. " + fex.Message);
        }
    }
}

UI.EndProgressBar();

// ---------- Results ----------
double floorWidth = (shaftX1 - shaftX0) + 2 * floorOffset;
double floorLength = (shaftY1 - shaftY0) + 2 * floorOffset;
Console.WriteLine("StairLab floors: " + floorsCreated + " of " + jsonLevels.Count + ", " +
    floorWidth.ToString("F1") + " x " + floorLength.ToString("F1") + " ft each.");
foreach (string warning in warnings) Console.WriteLine("WARNING: " + warning);

UI.CreateInfoForm("StairLab Floors Results", 540, 420, form =>
{
    form.AddHeader("Floors Complete", 18);
    form.AddLabel(floorsCreated + " of " + jsonLevels.Count + " floors created, " +
        floorWidth.ToString("F1") + " x " + floorLength.ToString("F1") + " ft (" +
        floorOffset.ToString("F1") + " ft beyond the shaft).", 12, isBold: true);
    foreach (string line in resultLines)
    {
        form.AddLabel(line, 11);
    }
    if (warnings.Any())
    {
        form.AddHeader("Warnings", 14);
        foreach (string warning in warnings)
        {
            form.AddLabel(warning, 11, isItalic: true);
        }
    }
});

// ========== Helper Methods ==========

private CurveLoop BuildRectLoop(double x0, double y0, double x1, double y1, double z)
{
    CurveLoop loop = new CurveLoop();
    XYZ a = new XYZ(x0, y0, z);
    XYZ b = new XYZ(x1, y0, z);
    XYZ c = new XYZ(x1, y1, z);
    XYZ d = new XYZ(x0, y1, z);
    loop.Append(Line.CreateBound(a, b));
    loop.Append(Line.CreateBound(b, c));
    loop.Append(Line.CreateBound(c, d));
    loop.Append(Line.CreateBound(d, a));
    return loop;
}

private double GetDouble(JToken token, string key, double fallback)
{
    if (token == null || token[key] == null) return fallback;
    double value;
    return double.TryParse(token[key].ToString(), out value) ? value : fallback;
}