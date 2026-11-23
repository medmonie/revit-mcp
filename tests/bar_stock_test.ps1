# Run with PowerShell 7. Compile the production geometry calculation without a Revit process.
$ErrorActionPreference = 'Stop'
$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..\plugin\Core\BarStock.cs')
$checks = @'
public static class StockChecks
{
    public static void Check(double run, double stock, double lap, double left, double right, int expected)
    {
        var pieces = BarStock.Pieces(100, 100 + run, stock, lap, left, right);
        if (pieces.Count != expected || pieces[0].a != 100 || pieces[expected-1].b != 100 + run)
            throw new Exception("Wrong count or extent");
        for (int i = 0; i < expected; i++)
        {
            var (a, b) = pieces[i];
            double length = b - a + (i == 0 ? left : 0) + (i == expected-1 ? right : 0);
            if (a >= b || length > stock + 1e-8) throw new Exception("Invalid piece length");
            if (i > 0 && Math.Abs(pieces[i-1].b - a - lap) > 1e-8) throw new Exception("Incorrect lap");
        }
    }
    public static void Run()
    {
        Check(6000, 2000, 1000, 0, 0, 5);
        Check(4200, 5000, 750, 400, 400, 1);
        Check(4300, 5000, 750, 400, 400, 2);
        Check(11300, 6000, 500, 100, 900, 3);
        Check(5000, 2000, 1500, 0, 0, 7);
        try { BarStock.Pieces(0, 6000, 2000, 2000, 0, 0); }
        catch (ArgumentException) { return; }
        throw new Exception("Impossible lap accepted");
    }
}
'@
Add-Type -TypeDefinition ("using System; using System.Collections.Generic;" + [Environment]::NewLine + $source + [Environment]::NewLine + $checks)
[RevitMcpBridge.StockChecks]::Run()
Write-Output 'Bar stock, lap, bend and impossible-input checks passed.'
