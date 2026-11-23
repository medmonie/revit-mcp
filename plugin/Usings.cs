// Project-wide usings. Implicit usings are off so that the same list is used for .NET Framework 4.8 (Revit 2024)
// and .NET 10 (Revit 2027).
global using System;
global using System.Collections.Generic;
global using System.Globalization;
global using System.IO;
global using System.Linq;
global using System.Text;
global using System.Threading;
global using Autodesk.Revit.DB;
global using Autodesk.Revit.UI;
global using static RevitMcpBridge.Args;
// A JSON object: request parameters and, often, results.
global using Obj = System.Collections.Generic.Dictionary<string, object>;
// Structural types (StructuralType, Rebar, ...) are written RStruct.Xxx.
global using RStruct = Autodesk.Revit.DB.Structure;
// System.IO.Path, never a Revit type of the same name.
global using IOPath = System.IO.Path;
