# ArchSmarter AI Experiments

A collection of AI-powered tools and workflows for architects and AEC professionals. Each experiment explores a different way to use AI (primarily Claude) to solve real design and documentation problems.

These aren't theoretical demos. They're working tools built from actual project needs.

## What's in Here

| Experiment | What It Is | Includes |
|---|---|---|
| [StairLab](./stairlab/) | Parametric stair designer with IBC 2021 code checking | HTML tool |
| [Parking Optimizer](./parking-optimizer/) | Maximizes stall count within a lot boundary | HTML tool, Claude Skill |
| [Twisting Tower](./twisting-tower/) | Parametric tower form generator | HTML tool |
| [Panel Configurator](./panel-configurator/) | Adaptive facade panel layout tool | HTML tool |
| [Family Generator](./family-generator/) | Image/description to Revit family pipeline | Claude Skill, Launchpad script, example JSON |
| [Floor Plan Generator](./floor-plan-generator/) | Sketch to Revit floor plan pipeline | Claude Skill, Launchpad script, example JSON |
| [Detail View Generator](./detail-view-generator/) | Construction detail image to Revit drafting view | Claude Skill, Launchpad script, example JSON |

Also see [Design Intent Pattern](./design-intent-pattern/) for the concept that ties the Skill-to-Revit pipelines together.

## How to Use This Repo

### If you just want to grab the files

1. Click the green **Code** button at the top of this page
2. Select **Download ZIP**
3. Unzip the folder wherever you want it

That's it. No Git required.

### Running the HTML tools

Open any `index.html` file in your browser (Chrome, Edge, Firefox). They're single-file, self-contained tools with no build step and no dependencies. Double-click the file and it runs.

### Using the Claude Skills

1. In [claude.ai](https://claude.ai), create a new Project (or open an existing one)
2. Add the `SKILL.md` file to the Project Knowledge
3. Start a conversation inside that Project

Each experiment's README explains what the Skill does and how to prompt it.

### Using the Launchpad Scripts

1. Copy the `.cs` file into your Launchpad scripts directory
2. The Skill generates a JSON file; the Launchpad script reads it and creates geometry in Revit
3. See each experiment's README for the full workflow

## What's the Design Intent Pattern?

Several experiments here follow the same architecture: a Claude Skill generates a structured JSON file describing *what* to build, and a Launchpad C# script executes *how* to build it inside Revit. The JSON acts as an intermediate format that separates design intent from Revit API implementation.

This means you can iterate on the design in Claude without touching C#, and the executor scripts stay stable across different inputs. Read more in [design-intent-pattern/README.md](./design-intent-pattern/README.md).

## About

These experiments come from [ArchSmarter](https://archsmarter.com), where I write about technology and automation for architects. If you're interested in building tools like these:

- **[Revit Add-in Academy](https://archsmarter.com/academy)** covers Revit API development and C# scripting
- **[Claude Workflows for Architects](https://archsmarter.com/cwa)** covers using Claude for AEC workflows, including Skills and the Design Intent Pattern
- **[Thursday Top 5 Newsletter](https://archsmarter.com/subscribe)** is a weekly roundup of AEC tech news and tips

## License

These files are provided for educational and personal use. Feel free to modify them for your own projects.
