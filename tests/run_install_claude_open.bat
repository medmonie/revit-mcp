@echo off
rem Test helper: installs the built package while Claude Desktop stays open (it keeps the session to this PC); never waits for a key.
call "%~dp0..\dist\RevitMcp\install.bat" /claude_open /no_pause
