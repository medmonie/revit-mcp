## Install (no Python, no build)

1. Close Revit.
2. Download **RevitMcp.zip** below, unzip it and double-click **install.bat** as the Windows user who runs Revit
   (not *Run as administrator*: it installs into the current user's profile and stops if it runs as another account).
   It installs the add-in for Revit 2024 and/or 2027, the MCP server (one exe), and adds `revit` to Claude Desktop and
   Codex (your other servers are kept; the old settings file is backed up).
3. Restart Claude Desktop completely, start Revit, choose **Always Load** when asked, open a model and ask
   *"Ping Revit and tell me which model is open."* (it shows 0.17.0).

ChatGPT desktop: Settings > MCP servers > Add server, name `revit`, STDIO, command = the server path install.bat prints.
Uninstall: `uninstall.bat` from the same zip. Full guide: [README](https://github.com/medmonie/revit-mcp#readme).

## What is new since 0.14

**0.17 - modify and documentation depth, standards and data** (45 read actions, 118 edit operations)
- Flip, wall and beam end joins and extensions, hosted (face / work-plane) family placement.
- Groups and assemblies (create, place, ungroup, assembly views and parts lists), schedule filters / sorting / totals /
  formatting, rotated / angular / radial (2027) dimensions, spot coordinates, element geometry read.
- Global parameters (create, set, drive element parameters), material structural / thermal / appearance assets,
  keynotes and assembly codes, DWF / FBX / DGN / gbXML export, two more warning fixes.
- Several Revit windows: a second Revit no longer takes Claude over; with several open, edits wait until you say which
  Revit to use. Closing one or a crash no longer leaves "Revit is not reachable".
- Installer: shows the account it installs for and stops when it is not the desktop user (domain controller case).

**0.16 - linked models:** read elements, types, levels and grids inside loaded Revit links, in host or link coordinates.

**0.15 - no design in the bridge:** reinforcement and joist tools take only the engineer's values (no built-in covers,
spacings, laps or anchorages); a missing value is asked for.

Known limits: radial dimensions need Revit 2027; spot slopes cannot be created by any Revit API; DGN export needs a
DGN setup or seed file; gbXML needs an energy model set up in Revit.

Details: [CHANGELOG](https://github.com/medmonie/revit-mcp/blob/main/CHANGELOG.md).

**RevitMcp-source.zip** is the clean source for building on another PC (needs the .NET 10 SDK and Python 3.10+).

Licence: PolyForm Noncommercial 1.0.0 - free for non-commercial use; commercial use needs written permission.
