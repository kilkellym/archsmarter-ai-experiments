// ==========================================================================
// StairLab Importer
// Recreates a StairLab JSON export as native Revit stairs, landings, and
// host-placed railings.
//
// Pipeline:
//   1. Marker transaction disables the Launchpad auto-transaction so
//      StairsEditScope can run (the scope cannot start while the document
//      is modifiable).
//   2. User selects the StairLab JSON, placement mode, and railing option.
//   3. JSON types map to Revit StairsType / RailingType via comboboxes,
//      following the StairLab type_mappings pattern (revit_type: null).
//      By default the script duplicates the base stair type into a matched
//      StairLab type with the design's riser, tread, and width constraints
//      baked in, so Revit cannot fight the design geometry. Re-imports
//      reuse the matched type by name instead of duplicating again.
//   4. Levels are found by elevation or created.
//   5. Each stair builds inside its own StairsEditScope: straight runs from
//      path_start/path_end location lines, sketched landings from boundary
//      loops.
//   6. Railings: Revit auto-generates default railings when each edit
//      scope commits, so the script always removes those first, then
//      places the chosen type only when the railing option is on and the
//      design had railings.
//
// V1 limitations (by design, documented in the results dialog):
//   - Host railings apply ONE railing type to all sides of each stair.
//     Per-side mounts (left/right, floor/wall) in the JSON are not applied.
//   - Rail extension and connector geometry comes from the Revit railing
//     type, not from the JSON segment paths.
//   - All JSON coordinates are in feet (Revit internal units), so no unit
//     conversion is performed.
// ==========================================================================

using System.IO;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

string TransactionName = "Import StairLab Design";

// Opening and closing a transaction here negates the Launchpad
// auto-transaction. All further changes use explicit transactions.
using (Transaction markerTrans = new Transaction(doc, "StairLab Import Init"))
{
    markerTrans.Start();
    markerTrans.Commit();
}

// ---------- Form 1: file and options ----------
string fileLabel = "StairLab JSON file:";
string placementLabel = "Placement:";
string railingsLabel = "Create railings on stairs";
List<string> placementOptions = new List<string> { "Pick insertion point", "Project origin" };

var fileResults = UI.CreateCustomForm("StairLab Importer", 520, 380, form =>
{
    form.AddHeader("Import StairLab Design", 18);
    form.AddLabel("Recreates a StairLab export as native Revit stairs, landings, and railings.", 12, isItalic: true);
    form.AddFileSelector(fileLabel, "StairLab JSON (*.json)|*.json|All files (*.*)|*.*");
    form.AddRadioButtons(placementLabel, placementOptions, "Pick insertion point");
    form.AddCheckbox(railingsLabel, true);
});

if (!fileResults.Success) return;

string jsonPath = fileResults.GetStringResult(fileLabel);
string placementChoice = fileResults.GetStringResult(placementLabel);
bool createRailings = fileResults.GetBoolResult(railingsLabel);

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
JArray jsonStairs = design["stairs"] as JArray;
if (jsonLevels == null || jsonStairs == null || jsonStairs.Count == 0)
{
    TaskDialog.Show("Invalid File", "The file does not contain StairLab levels and stairs data.");
    return;
}

string jsonUnits = (string)(design["metadata"]?["units"]) ?? "feet";
if (jsonUnits != "feet")
{
    TaskDialog.Show("Units Error", "This importer expects coordinates in feet. The file declares: " + jsonUnits);
    return;
}

// Design-wide values used for stair type validation
double maxActualRiser = jsonStairs.Max(s => GetDouble(s, "actual_riser_height", 0));
double jsonTreadDepth = jsonStairs.Max(s => GetDouble(s, "tread_depth", 0));
double jsonRunWidth = jsonStairs.Max(s => GetDouble(s, "width", 0));

// ---------- Collect Revit types ----------
List<StairsType> stairsTypes = new FilteredElementCollector(doc)
    .OfClass(typeof(StairsType))
    .Cast<StairsType>()
    .OrderBy(t => t.Name)
    .ToList();

List<RailingType> railingTypes = new FilteredElementCollector(doc)
    .OfClass(typeof(RailingType))
    .Cast<RailingType>()
    .OrderBy(t => t.Name)
    .ToList();

if (!stairsTypes.Any())
{
    TaskDialog.Show("No Stair Types", "The document contains no stair types. Load a stair type and run again.");
    return;
}

if (createRailings && !railingTypes.Any())
{
    Console.WriteLine("No railing types found in the document. Railings will be skipped.");
    createRailings = false;
}

// ---------- Form 2: type mapping ----------
string stairTypeLabel = "Base stair type (monolithic_stair):";
string matchedTypeLabel = "Create matched StairLab type from the base (recommended)";
string railTypeLabel = "Railing type (all sides):";
string designSummary = jsonStairs.Count + " stairs, " + jsonLevels.Count + " levels, riser " +
    (maxActualRiser * 12).ToString("F2") + " in, tread " + (jsonTreadDepth * 12).ToString("F1") + " in";

var mapResults = UI.CreateCustomForm("StairLab Type Mapping", 540, 420, form =>
{
    form.AddHeader("Map JSON Types to Revit Types", 18);
    form.AddLabel(designSummary, 12);
    form.AddComboBox(stairTypeLabel, stairsTypes, t => t.Name);
    form.AddCheckbox(matchedTypeLabel, true);
    form.AddLabel("The matched type copies the base and sets max riser, min tread, and min run width to the design values, so the imported geometry matches exactly. Uncheck to use the base type as is.", 11, isItalic: true);
    if (createRailings)
    {
        form.AddComboBox(railTypeLabel, railingTypes, t => t.Name);
        form.AddLabel("Host railings apply one type to all sides. Per side mounts in the JSON are not applied in v1.", 11, isItalic: true);
    }
});

if (!mapResults.Success) return;

StairsType selectedStairType = mapResults.GetElementResult(stairTypeLabel) as StairsType;
bool createMatchedType = mapResults.GetBoolResult(matchedTypeLabel);
RailingType selectedRailType = createRailings ? mapResults.GetElementResult(railTypeLabel) as RailingType : null;

if (selectedStairType == null)
{
    TaskDialog.Show("Type Error", "No stair type was selected.");
    return;
}

List<string> warnings = new List<string>();

// ---------- Placement origin ----------
double originX = 0, originY = 0;
if (placementChoice == "Pick insertion point")
{
    try
    {
        // JSON coordinates are relative to the stair opening corner at (0,0).
        // Only X and Y are used; elevations come from the JSON levels.
        XYZ picked = uidoc.Selection.PickPoint("Pick the stair opening corner (insertion point)");
        originX = picked.X;
        originY = picked.Y;
    }
    catch (Exception)
    {
        Console.WriteLine("Point selection cancelled. Import aborted.");
        return;
    }
}

// ---------- Resolve the stair type: matched duplicate or base ----------
StairsType effectiveStairType = selectedStairType;
string typeNote = "Used stair type: " + selectedStairType.Name;

if (createMatchedType)
{
    using (Transaction typeTrans = new Transaction(doc, "StairLab Stair Type"))
    {
        typeTrans.Start();
        StairsType matchedType = FindOrCreateMatchedStairType(
            doc, stairsTypes, selectedStairType, maxActualRiser, jsonTreadDepth, jsonRunWidth, warnings);
        if (matchedType != null)
        {
            effectiveStairType = matchedType;
            typeNote = "Used matched stair type: " + matchedType.Name + " (base: " + selectedStairType.Name + ")";
        }
        typeTrans.Commit();
    }
}

// Validate whichever type the stairs will actually use. The matched type
// should pass silently; the base type may add drift warnings.
ValidateStairType(effectiveStairType, maxActualRiser, jsonTreadDepth, jsonRunWidth, warnings);

// ---------- Levels: find by elevation or create ----------
List<Level> existingLevels = new FilteredElementCollector(doc)
    .OfClass(typeof(Level))
    .Cast<Level>()
    .ToList();

Dictionary<string, Level> levelMap = new Dictionary<string, Level>();

using (Transaction levelTrans = new Transaction(doc, "StairLab Levels"))
{
    levelTrans.Start();
    foreach (JToken jsonLevel in jsonLevels)
    {
        string levelName = (string)jsonLevel["name"];
        double elevation = GetDouble(jsonLevel, "elevation", 0);
        Level level = FindOrCreateLevel(doc, existingLevels, levelName, elevation, warnings);
        if (level != null) levelMap[levelName] = level;
    }
    levelTrans.Commit();
}

// ---------- Create stairs, one StairsEditScope per flight ----------
List<string> resultLines = new List<string>();
List<ElementId> createdStairIds = new List<ElementId>();
List<JToken> createdStairJson = new List<JToken>();

UI.StartProgressBar(jsonStairs.Count, "stairs", showCancelButton: true);

for (int i = 0; i < jsonStairs.Count; i++)
{
    if (UI.IsProgressCancelled()) break;

    JToken jsonStair = jsonStairs[i];
    string stairName = (string)jsonStair["name"] ?? ("Stair " + (i + 1));
    UI.UpdateProgressBar(i, "Creating " + stairName);

    string baseLevelName = (string)jsonStair["base_level"];
    string topLevelName = (string)jsonStair["top_level"];
    if (!levelMap.ContainsKey(baseLevelName) || !levelMap.ContainsKey(topLevelName))
    {
        warnings.Add(stairName + ": levels not found, skipped.");
        continue;
    }

    Level baseLevel = levelMap[baseLevelName];
    Level topLevel = levelMap[topLevelName];
    int expectedRisers = (int)GetDouble(jsonStair, "riser_count", 0);
    double runWidth = GetDouble(jsonStair, "width", 3.0);

    try
    {
        ElementId newStairsId = ElementId.InvalidElementId;

        using (StairsEditScope stairsScope = new StairsEditScope(doc, "Create " + stairName))
        {
            newStairsId = stairsScope.Start(baseLevel.Id, topLevel.Id);

            using (Transaction componentTrans = new Transaction(doc, "Stair Components"))
            {
                componentTrans.Start();

                Stairs stairsElement = doc.GetElement(newStairsId) as Stairs;
                if (stairsElement != null)
                {
                    stairsElement.ChangeTypeId(effectiveStairType.Id);
                    TrySetDesiredRisers(stairsElement, expectedRisers, stairName, warnings);
                }

                // Runs: the location line sits at the run's base elevation
                JArray jsonRuns = jsonStair["runs"] as JArray;
                if (jsonRuns != null)
                {
                    foreach (JToken jsonRun in jsonRuns)
                    {
                        double baseElev = GetDouble(jsonRun, "base_elevation", 0);
                        JArray ps = jsonRun["path_start"] as JArray;
                        JArray pe = jsonRun["path_end"] as JArray;
                        if (ps == null || pe == null) continue;

                        XYZ start = new XYZ((double)ps[0] + originX, (double)ps[1] + originY, baseElev);
                        XYZ end = new XYZ((double)pe[0] + originX, (double)pe[1] + originY, baseElev);
                        if (start.DistanceTo(end) < 0.01)
                        {
                            warnings.Add(stairName + " " + (string)jsonRun["name"] + ": run path too short, skipped.");
                            continue;
                        }

                        Line locationLine = Line.CreateBound(start, end);
                        StairsRun newRun = StairsRun.CreateStraightRun(
                            doc, newStairsId, locationLine, StairsRunJustification.Center);
                        TrySetRunWidth(newRun, runWidth, stairName, warnings);
                    }
                }

                // Landings: sketched from the boundary loops at their elevation
                JArray jsonLandings = jsonStair["landings"] as JArray;
                if (jsonLandings != null)
                {
                    foreach (JToken jsonLanding in jsonLandings)
                    {
                        double landingElev = GetDouble(jsonLanding, "elevation", 0);
                        CurveLoop landingLoop = BuildBoundaryLoop(
                            jsonLanding["boundary"] as JArray, originX, originY);
                        if (landingLoop == null)
                        {
                            warnings.Add(stairName + " " + (string)jsonLanding["name"] + ": invalid boundary, skipped.");
                            continue;
                        }
                        try
                        {
                            StairsLanding.CreateSketchedLanding(doc, newStairsId, landingLoop, landingElev);
                        }
                        catch (Exception lex)
                        {
                            warnings.Add(stairName + " " + (string)jsonLanding["name"] + ": " + lex.Message);
                        }
                    }
                }

                componentTrans.Commit();
            }

            stairsScope.Commit(new StairsImportFailuresPreprocessor());
        }

        // Report riser fidelity: Revit derives risers from the type
        // constraints, so drift from the JSON design must be visible
        Stairs createdStairs = doc.GetElement(newStairsId) as Stairs;
        int actualRisers = TryGetActualRisers(createdStairs);
        string riserNote = actualRisers >= 0
            ? actualRisers + " risers (design: " + expectedRisers + ")"
            : "(riser count unavailable, design: " + expectedRisers + ")";
        if (actualRisers >= 0 && actualRisers != expectedRisers)
        {
            warnings.Add(stairName + ": riser count drifted from the design. Check the stair type max riser height.");
        }

        resultLines.Add(stairName + ": created (Id " + newStairsId + "), " + riserNote);
        createdStairIds.Add(newStairsId);
        createdStairJson.Add(jsonStair);
    }
    catch (Exception ex)
    {
        warnings.Add(stairName + ": failed. " + ex.Message);
        Console.WriteLine(stairName + " exception: " + ex);
    }
}

UI.EndProgressBar();

// ---------- Railings: replace or remove the auto-generated set ----------
// Revit automatically adds default railings to a stair when its edit
// scope commits, so they must be handled on every created stair:
//   - railing option on and the design has rails: delete the autos,
//     then create the chosen type (Railing.Create requires the stair
//     to have no associated railings)
//   - railing option off, or the design had rails off: delete the autos
int railingsCreated = 0;
int autoRailingsRemoved = 0;
if (createdStairIds.Any())
{
    using (Transaction railTrans = new Transaction(doc, "StairLab Railings"))
    {
        railTrans.Start();
        for (int i = 0; i < createdStairIds.Count; i++)
        {
            Stairs hostStairs = doc.GetElement(createdStairIds[i]) as Stairs;
            if (hostStairs == null) continue;

            try
            {
                ICollection<ElementId> autoRailings = hostStairs.GetAssociatedRailings();
                foreach (ElementId railingId in autoRailings)
                {
                    doc.Delete(railingId);
                    autoRailingsRemoved++;
                }
            }
            catch (Exception aex)
            {
                warnings.Add("Stair Id " + createdStairIds[i] + ": could not remove the auto railings. " + aex.Message);
            }

            JArray jsonRailings = createdStairJson[i]["railings"] as JArray;
            bool wantRailings = createRailings && selectedRailType != null &&
                jsonRailings != null && jsonRailings.Count > 0;
            if (!wantRailings) continue; // option unchecked or rails off in the design

            try
            {
                Railing.Create(doc, createdStairIds[i], selectedRailType.Id, RailingPlacementPosition.Treads);
                railingsCreated++;
            }
            catch (Exception rex)
            {
                warnings.Add("Railing on stair Id " + createdStairIds[i] + ": " + rex.Message);
            }
        }
        railTrans.Commit();
    }
}

// ---------- Results ----------
Console.WriteLine("StairLab import: " + createdStairIds.Count + " stairs, " + railingsCreated +
    " railed, " + autoRailingsRemoved + " auto railings removed.");
foreach (string warning in warnings) Console.WriteLine("WARNING: " + warning);

UI.CreateInfoForm("StairLab Import Results", 560, 520, form =>
{
    form.AddHeader("Import Complete", 18);
    form.AddLabel(createdStairIds.Count + " of " + jsonStairs.Count + " stairs created. " +
        railingsCreated + " stairs received railings.", 12, isBold: true);
    if (autoRailingsRemoved > 0)
    {
        form.AddLabel(autoRailingsRemoved + " auto generated railings were removed (Revit adds these when stairs are created).", 11);
    }
    form.AddLabel(typeNote, 11);
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

// ========== Classes ==========

// Swallows warnings raised when the edit scope commits, so five flights
// do not produce five popup dialogs. Errors still surface normally.
class StairsImportFailuresPreprocessor : IFailuresPreprocessor
{
    public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
    {
        IList<FailureMessageAccessor> failures = accessor.GetFailureMessages();
        foreach (FailureMessageAccessor failure in failures)
        {
            if (accessor.GetSeverity() == FailureSeverity.Warning)
            {
                accessor.DeleteWarning(failure);
            }
        }
        return FailureProcessingResult.Continue;
    }
}

// ========== Helper Methods ==========

private StairsType FindOrCreateMatchedStairType(Document document, List<StairsType> existingTypes,
    StairsType baseType, double riserFt, double treadFt, double widthFt, List<string> warnings)
{
    // Name encodes the constraints, which makes re-imports idempotent:
    // the same design finds and reuses its type instead of duplicating
    string matchedName = "StairLab " + (widthFt * 12).ToString("0.#") + "in R" +
        (riserFt * 12).ToString("0.##") + " T" + (treadFt * 12).ToString("0.##");

    StairsType existing = existingTypes.FirstOrDefault(t => t.Name == matchedName);
    if (existing != null) return existing;

    StairsType matchedType = null;
    try
    {
        matchedType = baseType.Duplicate(matchedName) as StairsType;
        if (matchedType == null)
        {
            warnings.Add("Could not duplicate the base stair type. Using '" + baseType.Name + "' as is.");
            return null;
        }

        // Bake the design constraints into the type. The small riser
        // tolerance absorbs floating point drift from the JSON round trip
        matchedType.MaxRiserHeight = riserFt + 0.001;
        matchedType.MinTreadDepth = treadFt;
        matchedType.MinRunWidth = Math.Min(widthFt, matchedType.MinRunWidth);
        return matchedType;
    }
    catch (Exception ex)
    {
        // A constraint setter failed; do not leave a half-configured type
        Console.WriteLine("Matched type creation failed: " + ex.Message);
        if (matchedType != null)
        {
            try { document.Delete(matchedType.Id); } catch (Exception) { }
        }
        warnings.Add("Could not create a matched stair type (" + ex.Message +
            "). Falling back to '" + baseType.Name + "'; check the drift warnings.");
        return null;
    }
}

private Level FindOrCreateLevel(Document document, List<Level> existingLevels,
    string levelName, double elevation, List<string> warnings)
{
    // Match by elevation, not by name: the project's Level 1 may not sit
    // at the JSON's datum, and elevation is what the geometry depends on
    Level match = existingLevels.FirstOrDefault(l => Math.Abs(l.Elevation - elevation) < 0.005);
    if (match != null) return match;

    Level newLevel = Level.Create(document, elevation);
    try
    {
        newLevel.Name = levelName;
    }
    catch (Exception)
    {
        // Name collision with a level at a different elevation
        newLevel.Name = "StairLab " + levelName;
        warnings.Add("Level name '" + levelName + "' was taken; created 'StairLab " + levelName + "' instead.");
    }
    existingLevels.Add(newLevel);
    return newLevel;
}

private CurveLoop BuildBoundaryLoop(JArray boundary, double originX, double originY)
{
    if (boundary == null || boundary.Count < 3) return null;

    // Build the loop flat at Z = 0. CreateSketchedLanding's baseElevation
    // sets the height; carrying elevation in the sketch too double-counts it.
    List<XYZ> points = new List<XYZ>();
    foreach (JToken pt in boundary)
    {
        JArray coords = pt as JArray;
        if (coords == null || coords.Count < 2) return null;
        points.Add(new XYZ((double)coords[0] + originX, (double)coords[1] + originY, 0.0));
    }

    CurveLoop loop = new CurveLoop();
    for (int i = 0; i < points.Count; i++)
    {
        XYZ a = points[i];
        XYZ b = points[(i + 1) % points.Count];
        if (a.DistanceTo(b) < 0.01) return null; // degenerate edge
        loop.Append(Line.CreateBound(a, b));
    }
    return loop;
}

private double GetDouble(JToken token, string key, double fallback)
{
    if (token == null || token[key] == null) return fallback;
    double value;
    return double.TryParse(token[key].ToString(), out value) ? value : fallback;
}

private void ValidateStairType(StairsType stairsType, double actualRiserFt,
    double treadDepthFt, double runWidthFt, List<string> warnings)
{
    // The type's constraints drive Revit's riser computation. If the type
    // is tighter than the design, Revit adds risers and the geometry drifts.
    try
    {
        if (stairsType.MaxRiserHeight + 0.0001 < actualRiserFt)
        {
            warnings.Add("Stair type max riser (" + (stairsType.MaxRiserHeight * 12).ToString("F2") +
                " in) is below the design riser (" + (actualRiserFt * 12).ToString("F2") +
                " in). Revit will add risers. Edit the type or pick another.");
        }
        if (stairsType.MinTreadDepth - 0.0001 > treadDepthFt)
        {
            warnings.Add("Stair type min tread (" + (stairsType.MinTreadDepth * 12).ToString("F1") +
                " in) exceeds the design tread (" + (treadDepthFt * 12).ToString("F1") + " in).");
        }
        if (stairsType.MinRunWidth - 0.0001 > runWidthFt)
        {
            warnings.Add("Stair type min run width exceeds the design width.");
        }
    }
    catch (Exception)
    {
        warnings.Add("Could not read stair type constraints for validation. Verify max riser and min tread manually.");
    }
}

private void TrySetDesiredRisers(Stairs stairsElement, int risers, string stairName, List<string> warnings)
{
    if (risers <= 0) return;
    try
    {
        stairsElement.DesiredRisersNumber = risers;
    }
    catch (Exception)
    {
        warnings.Add(stairName + ": could not set the desired riser count; Revit will derive it from the type.");
    }
}

private void TrySetRunWidth(StairsRun stairsRun, double widthFt, string stairName, List<string> warnings)
{
    try
    {
        stairsRun.ActualRunWidth = widthFt;
    }
    catch (Exception)
    {
        warnings.Add(stairName + ": could not set the run width; the type default applies.");
    }
}

private int TryGetActualRisers(Stairs stairsElement)
{
    if (stairsElement == null) return -1;
    try
    {
        return stairsElement.ActualRisersNumber;
    }
    catch (Exception)
    {
        return -1;
    }
}
