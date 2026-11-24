namespace RevitMcpBridge;

/// <summary>
/// A read inside a linked Revit model: the link instance, its loaded document and the transform that places it in the
/// host. Read actions with a 'link' parameter look up elements in Doc and report coordinates through Tf ('host', the
/// default) or in the linked model's own internal coordinates ('link').
/// </summary>
internal sealed class LinkScope
{
    public RevitLinkInstance Instance;
    public Document Doc;
    public Transform Total;      // link internal coordinates -> host internal coordinates
    public bool HostCoordinates;

    /// <summary>The transform applied to reported points: the link transform, or identity for 'link' coordinates.</summary>
    public Transform Tf => HostCoordinates ? Total : Transform.Identity;

    /// <summary>The link named by the 'link' parameter, or null when it is absent (read the host model).</summary>
    public static LinkScope Of(Document host, Obj p)
    {
        string given = Ref(p, "link");
        if (string.IsNullOrWhiteSpace(given)) return null;
        var inst = Resolve(host, given.Trim());
        var doc = inst.GetLinkDocument()
                  ?? throw new ArgumentException($"Link '{inst.Name}' (id {inst.Id.Value}) is not loaded, so its elements cannot be read. Reload it in Manage Links.");
        bool host2 = Choice(p, "coordinates", "host", "host", "link") == "host";
        return new LinkScope { Instance = inst, Doc = doc, Total = inst.GetTotalTransform(), HostCoordinates = host2 };
    }

    /// <summary>The document to read: the link's when 'link' is given, else the host.</summary>
    public static Document DocOf(Document host, Obj p, out LinkScope link)
    {
        link = Of(host, p);
        return link?.Doc ?? host;
    }

    private static string Bare(string s)
    {
        s = (s ?? "").Trim();
        return s.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) ? s.Substring(0, s.Length - 4) : s;
    }

    /// <summary>A link instance by instance id, link type id (when the type is placed once), instance name or file name.</summary>
    public static RevitLinkInstance Resolve(Document host, string given)
    {
        var all = new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>().ToList();
        if (all.Count == 0) throw new ArgumentException("This model has no Revit links.");
        string Listing() => string.Join("; ", all.Take(12).Select(i => $"{i.Id.Value} = {i.Name}"));

        if (long.TryParse(given, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
        {
            var byId = all.FirstOrDefault(i => i.Id.Value == n);
            if (byId != null) return byId;
            var ofType = all.Where(i => i.GetTypeId().Value == n).ToList();
            if (ofType.Count == 1) return ofType[0];
            if (ofType.Count > 1)
                throw new ArgumentException($"Link type {n} is placed {ofType.Count} times; give the instance id: " + string.Join(", ", ofType.Select(i => i.Id.Value)) + ".");
        }

        var hits = all.Where(i => string.Equals(i.Name?.Trim(), given, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hits.Count == 0)
        {
            string want = Bare(given);
            hits = all.Where(i => host.GetElement(i.GetTypeId()) is RevitLinkType t && string.Equals(Bare(t.Name), want, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (hits.Count == 1) return hits[0];
        if (hits.Count > 1)
            throw new ArgumentException($"'{given}' is placed {hits.Count} times; give the instance id: " + string.Join(", ", hits.Select(i => i.Id.Value)) + ".");
        throw new ArgumentException($"No Revit link '{given}'. Links (instance id = name): {Listing()}. Use list_links.");
    }

    // ---------------------------------------------------------------- transform output

    /// <summary>Rotation of a transform about the vertical axis, degrees.</summary>
    public static double PlanRotationDeg(Transform t) => U.Deg(Math.Atan2(t.BasisX.Y, t.BasisX.X));

    /// <summary>True when the link's Z axis is not vertical (rotated out of plan); then plan rotation and level elevations are approximate.</summary>
    public static bool Tilted(Transform t) => !t.BasisZ.IsAlmostEqualTo(XYZ.BasisZ);

    public static Obj TransformOut(Transform t)
    {
        var o = new Obj { ["origin"] = U.Xyz(t.Origin), ["rotationDeg"] = PlanRotationDeg(t) };
        if (Tilted(t)) o["tilted"] = true;
        if (t.HasReflection) o["mirrored"] = true;
        return o;
    }

    /// <summary>What a result says about the link it read.</summary>
    public Obj Info() => new Obj
    {
        ["id"] = Instance.Id.Value, ["name"] = Instance.Name, ["document"] = Doc.Title,
        ["coordinates"] = HostCoordinates ? "host" : "link", ["transform"] = TransformOut(Total),
        ["note"] = "Element ids, levels and types belong to the linked model. " + (HostCoordinates
            ? "Points are in host coordinates (link transform applied); boxes are the axis-aligned box of the transformed corners."
            : "Points are in the linked model's own internal coordinates."),
    };

    /// <summary>An elevation in the linked model as a height in the reported coordinates (exact unless the link is tilted).</summary>
    public double Z(double linkZ) => Tf.OfPoint(new XYZ(0, 0, linkZ)).Z;
}
