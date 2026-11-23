## Install (no Python, no build)

1. Close Revit.
2. Download **RevitMcp.zip** below, unzip it and double-click **install.bat**. It installs the add-in for Revit 2024
   and/or 2027, the MCP server (one exe), and adds `revit` to Claude Desktop and Codex (your other servers are kept;
   the old settings file is backed up).
3. Restart Claude Desktop, start Revit, choose **Always Load** when asked, open a model and ask
   *"Ping Revit and tell me which model is open."*

ChatGPT desktop: Settings > MCP servers > Add server, name `revit`, STDIO, command = the server path install.bat prints.
Uninstall: `uninstall.bat` from the same zip. Full guide: [README](https://github.com/medmonie/revit-mcp#readme).

## What is new in 0.14

- Installer package: `install.bat` / `uninstall.bat`, the server as `revit-mcp-server.exe`, automatic Claude Desktop
  and Codex configuration, internet mark removed from the unzipped files so Revit 2024 loads the add-in.
- 0.13: model management (model health, shared parameters, purge with a safe preview, copy standards from another
  model, worksets, phases).
- 0.12: reinforcement detailing (anchorage into supports, laps, bends, footing feet, closer ties and stirrups at the
  ends, U bars) and the bar bending schedule.

Details: [CHANGELOG](https://github.com/medmonie/revit-mcp/blob/main/CHANGELOG.md).

**RevitMcp-source.zip** is the clean source for building on another PC (needs the .NET 10 SDK and Python 3.10+).

Licence: PolyForm Noncommercial 1.0.0 - free for non-commercial use; commercial use needs written permission.
