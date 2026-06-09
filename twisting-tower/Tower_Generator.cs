// Generate a high-rise tower in Revit from a Tower Generator config JSON file.
// The JSON supplies all geometry (shape, dimensions, floors, twist, taper).
// The user selects the JSON file plus the Revit floor type and optional curtain wall type.
string TransactionName = "Generate High-Rise Tower from JSON";

// ========== UI Labels ==========
string jsonFileLabel = "Tower config JSON:";
string floorTypeLabel = "Floor Type:";
string curtainWallLabel = "Generate Curtain Wall";
string curtainWallTypeLabel = "Curtain Wall Type:";

// ========== Collect Revit types for the dropdowns ==========
List<FloorType> floorTypes = GetAllFloorTypes();
List<WallType> curtainWallTypes = GetCurtainWallTypes();

if (floorTypes.Count == 0)
{
    TaskDialog.Show("Error", "No floor types found in this project. Create at least one floor type before running this script.");
    return;
}

// ========== Show the form ==========
var results = UI.CreateCustomForm("Tower Generator (from JSON)", 460, 560, form =>
{
    form.AddHeader("Tower Generator", 18);
    form.AddLabel("Select a tower config JSON exported from the Tower Generator, then choose the Revit types to build with.", 12, true);

    form.AddFileSelector(jsonFileLabel, "JSON Files (*.json)|*.json|All files (*.*)|*.*");

    form.AddComboBox(floorTypeLabel, floorTypes, ft => ft.Name);

    form.BeginExpander("Curtain Wall (optional)", false, 15);
    form.AddCheckbox(curtainWallLabel, false);
    if (curtainWallTypes.Count > 0)
    {
        form.AddComboBox(curtainWallTypeLabel, curtainWallTypes, wt => wt.Name);
    }
    else
    {
        form.AddLabel("No curtain wall types in project. The default system curtain wall will be used if enabled.", 12, true);
    }
    form.EndExpander();
});

if (!results.Success) return;

// ========== Read form results ==========
string jsonPath = results.GetStringResult(jsonFileLabel);
FloorType floorType = results.GetElementResult(floorTypeLabel) as FloorType;
bool generateCurtainWall = results.GetBoolResult(curtainWallLabel);
WallType curtainWallType = curtainWallTypes.Count > 0
    ? results.GetElementResult(curtainWallTypeLabel) as WallType
    : null;

// ========== Validate file selection ==========
if (string.IsNullOrWhiteSpace(jsonPath))
{
    TaskDialog.Show("Error", "No JSON file was selected.");
    return;
}
if (!System.IO.File.Exists(jsonPath))
{
    TaskDialog.Show("Error", $"File not found:\n{jsonPath}");
    return;
}
if (floorType == null)
{
    TaskDialog.Show("Error", "Please select a valid floor type.");
    return;
}

// ========== Parse the config ==========
TowerConfig config;
try
{
    string jsonText = System.IO.File.ReadAllText(jsonPath);
    config = ParseTowerConfig(jsonText);
}
catch (Exception ex)
{
    TaskDialog.Show("Invalid JSON", $"Could not read the tower config:\n\n{ex.Message}");
    Console.WriteLine($"JSON parse error: {ex}");
    return;
}

// ========== Validate config values ==========
string validationError = ValidateConfig(config);
if (validationError != null)
{
    TaskDialog.Show("Invalid Config", validationError);
    return;
}

Console.WriteLine($"Loaded config: shape={config.FloorShape}, floors={config.NumFloors}, height={config.FloorHeight}, " +
                  $"L={config.Length}, W={config.Width}, twist={config.TwistAngle}, taper={config.TaperProfile} {config.TaperIntensity}%");

// ========== Confirm ==========
TaskDialog confirmDialog = new TaskDialog("Confirm Tower Creation");
confirmDialog.MainInstruction = "Create High-Rise Tower?";
confirmDialog.MainContent =
    $"From: {System.IO.Path.GetFileName(jsonPath)}\n\n" +
    $"- {config.NumFloors} floors\n" +
    $"- {config.FloorHeight}' floor to floor height\n" +
    $"- {config.Length}' x {config.Width}' {config.FloorShape.ToLower()} floor plan\n" +
    $"- {config.TwistAngle}° twist per floor\n" +
    $"- {config.TaperProfile} at {config.TaperIntensity}% intensity\n" +
    $"- Floor type: {floorType.Name}\n" +
    $"- {(generateCurtainWall ? $"With curtain wall ({(curtainWallType != null ? curtainWallType.Name : "default")})" : "No curtain wall")}\n\n" +
    "Continue?";
confirmDialog.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;
if (confirmDialog.Show() != TaskDialogResult.Yes)
{
    Console.WriteLine("User canceled operation.");
    return;
}

// ========== Build ==========
try
{
    CreateHighRiseTower(config, floorType, generateCurtainWall, curtainWallType);
}
catch (Exception ex)
{
    TaskDialog.Show("Error", $"Error creating tower: {ex.Message}");
    Console.WriteLine($"Exception: {ex}");
}

// ============================================================
//  Config model + parsing
// ============================================================

public class TowerConfig
{
    public string FloorShape;
    public int NumFloors;
    public double FloorHeight;
    public double Length;
    public double Width;
    public double TwistAngle;
    public string TaperProfile;
    public int TaperIntensity;
}

// Parse only the geometry fields. Extra keys (prompt, camera, material, etc.) are ignored,
// so a richer export from the Tower Generator will not break this script.
private TowerConfig ParseTowerConfig(string jsonText)
{
    using (System.Text.Json.JsonDocument docJson = System.Text.Json.JsonDocument.Parse(jsonText))
    {
        var root = docJson.RootElement;

        TowerConfig cfg = new TowerConfig();
        cfg.FloorShape     = GetString(root, "floorShape", "Rectangular");
        cfg.NumFloors      = GetInt(root, "numFloors", 0);
        cfg.FloorHeight    = GetDouble(root, "floorHeight", 0);
        cfg.Length         = GetDouble(root, "length", 0);
        cfg.Width          = GetDouble(root, "width", 0);
        cfg.TwistAngle     = GetDouble(root, "twistAngle", 0);
        cfg.TaperProfile   = GetString(root, "taperProfile", "No Taper");
        cfg.TaperIntensity = GetInt(root, "taperIntensity", 0);
        return cfg;
    }
}

private string GetString(System.Text.Json.JsonElement root, string name, string fallback)
{
    if (root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String)
        return el.GetString();
    return fallback;
}

// Numbers in JSON may arrive as int or floating point; read as double then convert.
private double GetDouble(System.Text.Json.JsonElement root, string name, double fallback)
{
    if (root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number)
        return el.GetDouble();
    return fallback;
}

private int GetInt(System.Text.Json.JsonElement root, string name, int fallback)
{
    if (root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.Number)
        return (int)Math.Round(el.GetDouble());
    return fallback;
}

// Returns an error message, or null if the config is valid.
private string ValidateConfig(TowerConfig c)
{
    List<string> validShapes = new List<string> { "Rectangular", "Elliptical", "Triangular" };
    List<string> validTapers = new List<string> { "No Taper", "Uniform Taper", "Stepped Taper", "Setback", "Spire", "Bulge Profile", "Hourglass Profile", "Crown", "S-Curve" };

    if (!validShapes.Contains(c.FloorShape))
        return $"Unsupported floor shape: \"{c.FloorShape}\".\nExpected one of: Rectangular, Elliptical, Triangular.";
    if (!validTapers.Contains(c.TaperProfile))
        return $"Unsupported taper profile: \"{c.TaperProfile}\".";
    if (c.NumFloors < 2)
        return $"Number of floors must be at least 2 (got {c.NumFloors}).";
    if (c.FloorHeight <= 0)
        return $"Floor height must be positive (got {c.FloorHeight}).";
    if (c.Length <= 0 || c.Width <= 0)
        return $"Length and width must be positive (got {c.Length} x {c.Width}).";
    if (c.TaperIntensity < 0 || c.TaperIntensity > 100)
        return $"Taper intensity must be between 0 and 100 (got {c.TaperIntensity}).";
    return null;
}

// ============================================================
//  Tower creation (geometry logic preserved from the original)
// ============================================================

private void CreateHighRiseTower(TowerConfig config, FloorType floorType, bool generateCurtainWall, WallType curtainWallType)
{
    int levelsCreated = 0;
    int floorsCreated = 0;
    int wallsCreated = 0;

    double baseHalfLength = config.Length / 2;
    double baseHalfWidth = config.Width / 2;

    // Base level
    Level baseLevel = GetOrCreateLevel("L1", 0);
    List<Level> levels = new List<Level> { baseLevel };
    levelsCreated++;

    for (int i = 1; i < config.NumFloors; i++)
    {
        double elevation = i * config.FloorHeight;
        Level level = GetOrCreateLevel($"L{i + 1}", elevation);
        levels.Add(level);
        levelsCreated++;
    }

    for (int i = 0; i < levels.Count; i++)
    {
        Level currentLevel = levels[i];
        Level nextLevel = (i < levels.Count - 1) ? levels[i + 1] : null;

        double rotation = i * config.TwistAngle;
        double scaleFactor = CalculateScaleFactor(i, config.NumFloors - 1, config.TaperProfile, config.TaperIntensity);
        double halfLength = baseHalfLength * scaleFactor;
        double halfWidth = baseHalfWidth * scaleFactor;

        Console.WriteLine($"Floor {i + 1}: scale={scaleFactor:F2}, dims={halfLength * 2:F2}' x {halfWidth * 2:F2}'");

        CurveLoop floorCurveLoop;
        if (config.FloorShape == "Rectangular")
            floorCurveLoop = CreateRectangle(halfLength, halfWidth, rotation);
        else if (config.FloorShape == "Triangular")
            floorCurveLoop = CreateTriangle(halfLength, halfWidth, rotation);
        else // Elliptical
            floorCurveLoop = CreateEllipse(halfLength, halfWidth, rotation);

        bool floorCreated = CreateFloorFromCurveLoop(currentLevel, floorCurveLoop, floorType, config.FloorShape);
        if (floorCreated)
        {
            floorsCreated++;

            if (generateCurtainWall && nextLevel != null)
            {
                if (CreateCurtainWallFromCurveLoop(floorCurveLoop, currentLevel, nextLevel, curtainWallType))
                    wallsCreated++;
            }
        }
    }

    StringBuilder message = new StringBuilder();
    message.AppendLine("High-rise tower creation complete.");
    message.AppendLine();
    message.AppendLine($"Created {levelsCreated} levels and {floorsCreated} floors ({config.FloorShape.ToLower()} shape)");
    message.AppendLine($"using {config.TaperProfile.ToLower()} profile.");
    if (generateCurtainWall)
    {
        message.AppendLine();
        message.AppendLine($"Added {wallsCreated} curtain wall sections.");
    }
    TaskDialog.Show("Tower Created", message.ToString());
}

// ============================================================
//  Geometry helpers
// ============================================================

// Calculate scaling factor based on taper profile and intensity.
private double CalculateScaleFactor(int floorIndex, int totalFloors, string taperProfile, int intensityPercentage)
{
    double intensity = intensityPercentage / 100.0;
    double position = (double)floorIndex / Math.Max(1, totalFloors); // 0 bottom, 1 top

    double scale;
    switch (taperProfile)
    {
        case "No Taper":
            scale = 1.0;
            break;
        case "Uniform Taper":
            scale = 1.0 - (position * intensity);
            break;
        case "Stepped Taper":
            int steps = 4;
            int step = (int)(position * steps);
            scale = 1.0 - (step * (intensity / steps));
            break;
        case "Setback":
            if (position < 0.5) scale = 1.0;
            else if (position < 0.8) scale = 1.0 - intensity * 0.4;
            else scale = 1.0 - intensity * 0.7;
            break;
        case "Spire":
            scale = 1.0 - intensity * Math.Pow(position, 2.5);
            break;
        case "Bulge Profile":
            double bulgePosition = 2.0 * Math.Abs(position - 0.5);
            scale = 1.0 + (intensity / 2) - (bulgePosition * intensity / 2);
            break;
        case "Hourglass Profile":
            double hourglassPosition = 2.0 * Math.Abs(position - 0.5);
            scale = 1.0 - (intensity / 2) + (hourglassPosition * intensity / 2);
            break;
        case "Crown":
            if (position < 0.6)
            {
                scale = 1.0 - (position / 0.6) * intensity * 0.4;
            }
            else
            {
                double flare = (position - 0.6) / 0.4;
                scale = (1.0 - intensity * 0.4) + flare * intensity * 0.5;
            }
            break;
        case "S-Curve":
            scale = 1.0 + intensity * 0.25 * Math.Sin(position * Math.PI * 3);
            break;
        default:
            scale = 1.0;
            break;
    }

    // Clamp to a small positive minimum so a floor never collapses to zero
    // (e.g. Uniform Taper at 100% drives the top floor to 0), which would make
    // Floor.Create throw on a degenerate boundary.
    return Math.Max(0.05, scale);
}

// Create a rectangle centered at origin, rotated about Z.
private CurveLoop CreateRectangle(double halfLength, double halfWidth, double rotationDegrees)
{
    double r = rotationDegrees * Math.PI / 180.0;

    XYZ p1 = RotatePointAroundZ(new XYZ(-halfLength, -halfWidth, 0), r);
    XYZ p2 = RotatePointAroundZ(new XYZ(halfLength, -halfWidth, 0), r);
    XYZ p3 = RotatePointAroundZ(new XYZ(halfLength, halfWidth, 0), r);
    XYZ p4 = RotatePointAroundZ(new XYZ(-halfLength, halfWidth, 0), r);

    CurveLoop loop = new CurveLoop();
    loop.Append(Line.CreateBound(p1, p2));
    loop.Append(Line.CreateBound(p2, p3));
    loop.Append(Line.CreateBound(p3, p4));
    loop.Append(Line.CreateBound(p4, p1));
    return loop;
}

// Create a triangle that fills the bounding box: apex on +Y (width axis),
// base spanning the full length on the -Y edge. Matches the Tower Generator HTML.
// Counterclockwise winding, consistent with the rectangle builder.
private CurveLoop CreateTriangle(double halfLength, double halfWidth, double rotationDegrees)
{
    double r = rotationDegrees * Math.PI / 180.0;

    XYZ apex  = RotatePointAroundZ(new XYZ(0, halfWidth, 0), r);
    XYZ baseR = RotatePointAroundZ(new XYZ(halfLength, -halfWidth, 0), r);
    XYZ baseL = RotatePointAroundZ(new XYZ(-halfLength, -halfWidth, 0), r);

    // Order baseL -> baseR -> apex for counterclockwise winding.
    CurveLoop loop = new CurveLoop();
    loop.Append(Line.CreateBound(baseL, baseR));
    loop.Append(Line.CreateBound(baseR, apex));
    loop.Append(Line.CreateBound(apex, baseL));
    return loop;
}

// Create an elliptical curve loop approximated with line segments.
private CurveLoop CreateEllipse(double halfLength, double halfWidth, double rotationDegrees)
{
    double r = rotationDegrees * Math.PI / 180.0;

    double perimeter = 2 * Math.PI * Math.Sqrt((halfLength * halfLength + halfWidth * halfWidth) / 2);
    int numSegments = Math.Max(16, (int)(perimeter / 1.0));
    numSegments = Math.Min(numSegments, 72);

    List<Curve> curves = new List<Curve>();
    XYZ firstPoint = null;
    XYZ prevPoint = null;

    for (int i = 0; i <= numSegments; i++)
    {
        double angle = (double)i / numSegments * 2 * Math.PI;
        XYZ pt = RotatePointAroundZ(new XYZ(halfLength * Math.Cos(angle), halfWidth * Math.Sin(angle), 0), r);

        if (i == 0)
        {
            firstPoint = pt;
            prevPoint = pt;
            continue;
        }

        // On the last point, close exactly onto the first to avoid a tiny gap.
        XYZ endPt = (i == numSegments) ? firstPoint : pt;

        if (prevPoint.DistanceTo(endPt) >= 0.01)
        {
            curves.Add(Line.CreateBound(prevPoint, endPt));
            prevPoint = endPt;
        }
    }

    CurveLoop loop = new CurveLoop();
    foreach (Curve c in curves) loop.Append(c);
    return loop;
}

// Create a floor from a single curve loop.
private bool CreateFloorFromCurveLoop(Level level, CurveLoop curveLoop, FloorType floorType, string shapeType)
{
    try
    {
        List<CurveLoop> floorBoundary = new List<CurveLoop> { curveLoop };
        Floor newFloor = Floor.Create(doc, floorBoundary, floorType.Id, level.Id);
        if (newFloor != null)
        {
            Console.WriteLine($"Created {shapeType.ToLower()} floor at level {level.Name}");
            return true;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating floor at level {level.Name}: {ex.Message}");
    }
    return false;
}

// Create curtain walls along each segment of the curve loop, spanning bottom to top level.
private bool CreateCurtainWallFromCurveLoop(CurveLoop curveLoop, Level bottomLevel, Level topLevel, WallType wallType)
{
    try
    {
        if (wallType == null)
        {
            wallType = GetDefaultCurtainWallType();
            if (wallType == null)
            {
                Console.WriteLine("No curtain wall type available. Skipping curtain wall.");
                return false;
            }
        }

        double height = topLevel.Elevation - bottomLevel.Elevation;
        foreach (Curve curve in curveLoop)
        {
            Wall.Create(doc, curve, wallType.Id, bottomLevel.Id, height, 0.0, false, true);
        }
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error creating curtain wall: {ex.Message}");
        return false;
    }
}

// Rotate a point around the Z axis.
private XYZ RotatePointAroundZ(XYZ point, double angle)
{
    double x = point.X * Math.Cos(angle) - point.Y * Math.Sin(angle);
    double y = point.X * Math.Sin(angle) + point.Y * Math.Cos(angle);
    return new XYZ(x, y, point.Z);
}

// Get or create a level with the given name and elevation.
private Level GetOrCreateLevel(string levelName, double elevation)
{
    Level level = new FilteredElementCollector(doc)
        .OfClass(typeof(Level))
        .Cast<Level>()
        .FirstOrDefault(l => l.Name == levelName);

    if (level == null)
    {
        level = Level.Create(doc, elevation);
        level.Name = levelName;
        Console.WriteLine($"Created level {levelName} at {elevation}'");
    }
    else if (!level.Elevation.Equals(elevation))
    {
        level.Elevation = elevation;
        Console.WriteLine($"Updated level {levelName} to {elevation}'");
    }
    return level;
}

// All floor types, sorted by name.
private List<FloorType> GetAllFloorTypes()
{
    return new FilteredElementCollector(doc)
        .OfClass(typeof(FloorType))
        .Cast<FloorType>()
        .OrderBy(ft => ft.Name)
        .ToList();
}

// All curtain wall types, sorted by name.
private List<WallType> GetCurtainWallTypes()
{
    return new FilteredElementCollector(doc)
        .OfClass(typeof(WallType))
        .Cast<WallType>()
        .Where(wt => wt.Kind == WallKind.Curtain)
        .OrderBy(wt => wt.Name)
        .ToList();
}

// First available curtain wall type, or null.
private WallType GetDefaultCurtainWallType()
{
    return new FilteredElementCollector(doc)
        .OfClass(typeof(WallType))
        .Cast<WallType>()
        .FirstOrDefault(wt => wt.Kind == WallKind.Curtain);
}