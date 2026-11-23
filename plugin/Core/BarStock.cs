namespace RevitMcpBridge;

internal static class BarStock
{
    // Lengths share the caller's unit. Only the first and last pieces carry the end legs.
    public static List<(double a, double b)> Pieces(double a, double b, double stock, double lap, double startLeg, double endLeg)
    {
        double run = b - a;
        if (run <= 0 || stock <= 0 || lap < 0 || startLeg < 0 || endLeg < 0)
            throw new ArgumentException("Check the clear run, stock length, lap and bend lengths.");
        var pieces = new List<(double, double)>();
        if (run + startLeg + endLeg <= stock)
        {
            pieces.Add((a, b));
            return pieces;
        }
        double advance = stock - lap;
        if (advance <= Math.Max(startLeg, endLeg) || run <= lap)
            throw new ArgumentException("The requested stock cannot accommodate the end bends and a progressing lap.");
        double count = Math.Max(2, Math.Ceiling((run + startLeg + endLeg - lap) / advance));
        if (count > 2000) throw new ArgumentException("A bar line requires more than 2000 pieces. Check stock_length and lap_d.");
        // Distribute the available straight run proportionally. Each piece advances within its own stock allowance.
        double fraction = (run - lap) / (count * advance - startLeg - endLeg);
        double start = a;
        for (int i = 0; i < (int)count; i++)
        {
            double capacity = advance - (i == 0 ? startLeg : 0);
            double end = i == (int)count - 1 ? b : start + lap + fraction * capacity;
            pieces.Add((start, end));
            start = end - lap;
        }
        return pieces;
    }
}
