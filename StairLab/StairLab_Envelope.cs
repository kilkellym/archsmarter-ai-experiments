// Coding Level: Standard
// ==========================================================================
// StairLab Envelope
// Builds the stair shaft enclosure from a StairLab JSON export:
//   - Shaft walls around the stair opening, base level to top level
//   - Individual shaft openings per level that cut floors between levels
//
// Companion to StairLabImporter. Run it with the same JSON file and the
// same insertion point so the envelope lands around the imported stairs.
//
// Geometry: the JSON carries the opening (what the shaft cuts) and
// envelope rectangles (what the walls trace). Newer exports carry one
// envelope per level, so the enclosure steps per story where a flight
// has a deeper floor landing, such as an area of refuge. Each story's
// walls trace the union of its two bounding level rectangles. Older
// exports fall back to a single wall tower on the overall envelope, or
// on the opening when no envelope exists at all.
//
// Wall placement: the wall interior face sits on the envelope line,
// offset outward by half the wall thickness, so the clear area the
// stairs were designed against is preserved.
//
// Notes:
//   - Walls are built on all four sides. The JSON carries no door data,
//     so place entry doors manually after import.
//   - This script uses the standard Launchpad transaction. No manual
//     transactions are created here because no edit scope is needed.
// ==========================================================================

using System.IO;
using Newtonsoft.Json.Linq;

string TransactionName = "Create StairLab Envelope";

// ---------- Form 1: file and options ----------
string fileLabel = "StairLab JSON file:";
string placementLabel = "Placement:";
string wallsLabel = "Create shaft walls";
string shaftLabel = "Create shaft opening (cuts floors)";
string wallTypeLabel = "Wall type:";
List<string> placementOptions = new List<string> { "Pick insertion point", "Project origin" };

List<WallType> wallTypes = new FilteredElementCollector(doc)
    .OfClass(typeof(WallType))
    .Cast<WallType>()
    .Where(t => t.Kind == WallKind.Basic)
    .OrderBy(t => t.Name)
    .ToList();

if (!wallTypes.Any())
{
    TaskDialog.Show("No Wall Types", "The document contains no basic wall types.");
    return;
}

var formResults = UI.CreateCustomForm("StairLab Envelope", 540, 420, form =>
{
    form.AddHeader("Create Stair Shaft Envelope", 18);
    form.AddLabel("Builds shaft walls and floor-cutting shaft openings from a StairLab export. Use the same JSON and insertion point as the stair import.", 12, isItalic: true);
    form.AddFileSelector(fileLabel, "StairLab JSON (*.json)|*.json|All files (*.*)|*.*");
    form.AddRadioButtons(placementLabel, placementOptions, "Pick insertion point");
    form.AddCheckbox(wallsLabel, true);
    form.AddComboBox(wallTypeLabel, wallTypes, t => t.Name);
    form.AddCheckbox(shaftLabel, true);
});

if (!formResults.Success) return;

string jsonPath = formResults.GetStringResult(fileLabel);
string placementChoice = formResults.GetStringResult(placementLabel);
bool createWalls = formResults.GetBoolResult(wallsLabel);
bool createShaft = formResults.GetBoolResult(shaftLabel);
WallType selectedWallType = formResults.GetElementResult(wallTypeLabel) as WallType;

if (!createWalls && !createShaft)
{
    TaskDialog.Show("Nothing To Do", "Both options are unchecked. Pick walls, the shaft opening, or both.");
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
if (jsonLevels == null || jsonLevels.Count < 2 || jsonOpenings == null || jsonOpenings.Count == 0)
{
    TaskDialog.Show("Invalid File", "The file does not contain StairLab levels and openings data.");
    return;
}

string jsonUnits = (string)(design["metadata"]?["units"]) ?? "feet";
if (jsonUnits != "feet")
{
    TaskDialog.Show("Units Error", "This script expects coordinates in feet. The file declares: " + jsonUnits);
    return;
}

// Validate that we have opening data for each level
List<string> warnings = new List<string>();
if (jsonOpenings.Count != jsonLevels.Count)
{
    warnings.Add("Opening count (" + jsonOpenings.Count + ") doesn't match level count (" + jsonLevels.Count + ").");
}

// The envelope rectangle includes the floor landing strips. Older
// exports have no envelope, so the walls fall back to the opening
double envelopeX0 = 0, envelopeY0 = 0;
double envelopeWidth = 0, envelopeLength = 0;
JToken jsonEnvelope = design["envelope"];
if (jsonEnvelope != null)
{
    JArray envelopeOrigin = jsonEnvelope["origin"] as JArray;
    if (envelopeOrigin != null && envelopeOrigin.Count >= 2)
    {
        envelopeX0 = (double)envelopeOrigin[0];
        envelopeY0 = (double)envelopeOrigin[1];
    }
    envelopeWidth = GetDouble(jsonEnvelope, "width", 0);
    envelopeLength = GetDouble(jsonEnvelope, "length", 0);
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
        Console.WriteLine("Point selection cancelled. Envelope aborted.");
        return;
    }
}

// ---------- Resolve bottom and top levels by elevation ----------
List<Level> existingLevels = new FilteredElementCollector(doc)
    .OfClass(typeof(Level))
    .Cast<Level>()
    .ToList();

double bottomElev = jsonLevels.Min(l => GetDouble(l, "elevation", 0));
double topElev = jsonLevels.Max(l => GetDouble(l, "elevation", 0));

Level bottomLevel = FindLevelByElevation(existingLevels, bottomElev);
Level topLevel = FindLevelByElevation(existingLevels, topElev);

if (bottomLevel == null || topLevel == null)
{
    TaskDialog.Show("Levels Missing",
        "No project levels found at elevation " +
        (bottomLevel == null ? bottomElev.ToString("F2") : topElev.ToString("F2")) +
        " ft.\n\nRun the StairLab Importer first so the stair levels exist, then run this script.");
    return;
}

// ---------- Shaft walls ----------
List<ElementId> createdWallIds = new List<ElementId>();
int wallStories = 0;
if (createWalls && selectedWallType != null)
{
    double halfThickness = selectedWallType.Width / 2.0;
    JArray jsonEnvelopes = design["envelopes"] as JArray;

    if (jsonEnvelopes != null && jsonEnvelopes.Count == jsonLevels.Count && jsonLevels.Count >= 2)
    {
        // Per-story walls: each story traces the union of the envelope
        // rectangles at its bounding levels, so refuge bump-outs are
        // enclosed where they exist and nowhere else
        for (int j = 0; j + 1 < jsonLevels.Count; j++)
        {
            double storyBaseElev = GetDouble(jsonLevels[j], "elevation", 0);
            double storyTopElev = GetDouble(jsonLevels[j + 1], "elevation", 0);
            Level storyBase = FindLevelByElevation(existingLevels, storyBaseElev);
            Level storyTop = FindLevelByElevation(existingLevels, storyTopElev);
            if (storyBase == null || storyTop == null)
            {
                warnings.Add("Story " + (j + 1) + ": levels not found, walls skipped.");
                continue;
            }

            double[] rectA = GetEnvelopeRect(jsonEnvelopes[j]);
            double[] rectB = GetEnvelopeRect(jsonEnvelopes[j + 1]);
            double ux0 = Math.Min(rectA[0], rectB[0]);
            double uy0 = Math.Min(rectA[1], rectB[1]);
            double ux1 = Math.Max(rectA[2], rectB[2]);
            double uy1 = Math.Max(rectA[3], rectB[3]);

            int created = CreateRectWalls(doc, selectedWallType,
                originX + ux0 - halfThickness, originY + uy0 - halfThickness,
                originX + ux1 + halfThickness, originY + uy1 + halfThickness,
                storyBase, storyTopElev - storyBaseElev, storyTop,
                createdWallIds, warnings);
            if (created > 0) wallStories++;
        }
    }
    else
    {
        // Older export: one wall tower on the overall envelope rectangle
        // Use first opening dimensions if no envelope exists
        if (envelopeWidth == 0 || envelopeLength == 0)
        {
            envelopeWidth = GetDouble(jsonOpenings[0], "width", 0);
            envelopeLength = GetDouble(jsonOpenings[0], "length", 0);
        }
        
        CreateRectWalls(doc, selectedWallType,
            originX + envelopeX0 - halfThickness, originY + envelopeY0 - halfThickness,
            originX + envelopeX0 + envelopeWidth + halfThickness, originY + envelopeY0 + envelopeLength + halfThickness,
            bottomLevel, topElev - bottomElev, topLevel,
            createdWallIds, warnings);
        if (createdWallIds.Any()) wallStories = 1;
    }
}

// ---------- Shaft openings per level ----------
List<ElementId> shaftIds = new List<ElementId>();
int openingsCreated = 0;
if (createShaft)
{
    // Create openings for each level (except the top level)
    for (int i = 0; i < jsonLevels.Count - 1; i++)
    {
        try
        {
            double levelElev = GetDouble(jsonLevels[i], "elevation", 0);
            double nextLevelElev = GetDouble(jsonLevels[i + 1], "elevation", 0);
            
            Level currentLevel = FindLevelByElevation(existingLevels, levelElev);
            Level nextLevel = FindLevelByElevation(existingLevels, nextLevelElev);
            
            if (currentLevel == null)
            {
                warnings.Add("Level at elevation " + levelElev.ToString("F2") + " ft not found, opening skipped.");
                continue;
            }
            
            if (nextLevel == null)
            {
                warnings.Add("Level at elevation " + nextLevelElev.ToString("F2") + " ft not found, opening skipped.");
                continue;
            }

            // Get opening data for this specific level
            JToken levelOpening = i < jsonOpenings.Count ? jsonOpenings[i] : jsonOpenings[0];
            
            // Get opening position - use level-specific position if available
            double openingX0 = 0, openingY0 = 0;
            JArray openingOrigin = levelOpening["origin"] as JArray;
            if (openingOrigin != null && openingOrigin.Count >= 2)
            {
                openingX0 = (double)openingOrigin[0];
                openingY0 = (double)openingOrigin[1];
            }
            
            // Get opening dimensions for this level
            double openingWidth = GetDouble(levelOpening, "width", 0);
            double openingLength = GetDouble(levelOpening, "length", 0);
            
            if (openingWidth < 0.5 || openingLength < 0.5)
            {
                warnings.Add("Level " + (i + 1) + ": invalid opening dimensions, skipped.");
                continue;
            }

            // Create opening profile at current level elevation
            CurveArray shaftProfile = new CurveArray();
            double z = levelElev;
            XYZ a = new XYZ(originX + openingX0, originY + openingY0, z);
            XYZ b = new XYZ(originX + openingX0 + openingWidth, originY + openingY0, z);
            XYZ c = new XYZ(originX + openingX0 + openingWidth, originY + openingY0 + openingLength, z);
            XYZ d = new XYZ(originX + openingX0, originY + openingY0 + openingLength, z);
            
            shaftProfile.Append(Line.CreateBound(a, b));
            shaftProfile.Append(Line.CreateBound(b, c));
            shaftProfile.Append(Line.CreateBound(c, d));
            shaftProfile.Append(Line.CreateBound(d, a));

            Opening shaftOpening = doc.Create.NewOpening(currentLevel, nextLevel, shaftProfile);
            shaftIds.Add(shaftOpening.Id);
            openingsCreated++;
        }
        catch (Exception sex)
        {
            warnings.Add("Level " + (i + 1) + " opening failed: " + sex.Message);
        }
    }
}

// ---------- Results ----------
Console.WriteLine("StairLab envelope: " + createdWallIds.Count + " walls, " + openingsCreated + " shaft openings created");
foreach (string warning in warnings) Console.WriteLine("WARNING: " + warning);

bool hasLandingStrip = envelopeWidth > 0 && envelopeLength > 0 && 
    (envelopeWidth > GetDouble(jsonOpenings[0], "width", 0) + 0.01 || 
     envelopeLength > GetDouble(jsonOpenings[0], "length", 0) + 0.01);

UI.CreateInfoForm("StairLab Envelope Results", 540, 450, form =>
{
    form.AddHeader("Envelope Complete", 18);
    form.AddLabel((createWalls ? createdWallIds.Count + " shaft walls created across " + wallStories + " stories. " : "Walls skipped. ") +
        (createShaft
            ? (openingsCreated > 0 ? openingsCreated + " shaft openings created (one per level)." : "No shaft openings created.")
            : "Shaft openings skipped."), 12, isBold: true);
    form.AddLabel("Levels: " + bottomLevel.Name + " to " + topLevel.Name, 11);
    
    if (openingsCreated > 0)
    {
        form.AddLabel("Each opening uses its level-specific dimensions and position from the JSON data.", 11);
    }
    
    if (hasLandingStrip)
    {
        form.AddLabel("Floor landing strips preserved: shaft openings cut only the stair opening areas, so slab at entry sides remains at every level.", 11, isItalic: true);
    }
    form.AddLabel("The JSON carries entry side data but no door positions, so place entry doors manually.", 11, isItalic: true);
    
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

private Level FindLevelByElevation(List<Level> levels, double elevation)
{
    return levels.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.005);
}

private double[] GetEnvelopeRect(JToken jsonEnvelope)
{
    // Returns [x0, y0, x1, y1] in design coordinates
    double rx0 = 0, ry0 = 0;
    JArray rectOrigin = jsonEnvelope["origin"] as JArray;
    if (rectOrigin != null && rectOrigin.Count >= 2)
    {
        rx0 = (double)rectOrigin[0];
        ry0 = (double)rectOrigin[1];
    }
    return new double[] {
        rx0, ry0,
        rx0 + GetDouble(jsonEnvelope, "width", 0),
        ry0 + GetDouble(jsonEnvelope, "length", 0)
    };
}

private int CreateRectWalls(Document document, WallType wallType,
    double x0, double y0, double x1, double y1,
    Level baseLevel, double height, Level topLevel,
    List<ElementId> createdIds, List<string> warnings)
{
    double z = baseLevel.Elevation;
    List<Line> centerlines = new List<Line>
    {
        Line.CreateBound(new XYZ(x0, y0, z), new XYZ(x1, y0, z)),
        Line.CreateBound(new XYZ(x1, y0, z), new XYZ(x1, y1, z)),
        Line.CreateBound(new XYZ(x1, y1, z), new XYZ(x0, y1, z)),
        Line.CreateBound(new XYZ(x0, y1, z), new XYZ(x0, y0, z))
    };

    int created = 0;
    foreach (Line centerline in centerlines)
    {
        try
        {
            Wall newWall = Wall.Create(document, centerline, wallType.Id,
                baseLevel.Id, height, 0, false, false);
            TrySetWallTopConstraint(newWall, topLevel, warnings);
            createdIds.Add(newWall.Id);
            created++;
        }
        catch (Exception wex)
        {
            warnings.Add("Wall segment failed: " + wex.Message);
        }
    }
    return created;
}

private double GetDouble(JToken token, string key, double fallback)
{
    if (token == null || token[key] == null) return fallback;
    double value;
    return double.TryParse(token[key].ToString(), out value) ? value : fallback;
}

private void TrySetWallTopConstraint(Wall wall, Level topLevel, List<string> warnings)
{
    try
    {
        Parameter topConstraint = wall.get_Parameter(BuiltInParameter.WALL_HEIGHT_TYPE);
        if (topConstraint != null && !topConstraint.IsReadOnly)
        {
            topConstraint.Set(topLevel.Id);
        }
    }
    catch (Exception)
    {
        warnings.Add("Could not set the wall top constraint to " + topLevel.Name +
            "; the wall uses an explicit height instead.");
    }
}