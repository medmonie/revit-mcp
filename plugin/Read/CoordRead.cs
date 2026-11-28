namespace RevitMcpBridge;

/// <summary>Project base point, survey point, internal origin, shared coordinates and site location; for a link, whether
/// its shared coordinates agree with the host's.</summary>
internal static class CoordRead
{
    /// <summary>Internal coordinates -> shared coordinates of a document's active location, checked against the base points.</summary>
    private sealed class Shared
    {
        public Transform ToShared;          // internal -> shared
        public double CheckError;           // largest difference at the base points, feet
        public string CheckedAgainst;
    }

    private static Shared SharedOf(Document doc)
    {
        var location = doc.ActiveProjectLocation;
        var total = location.GetTotalTransform();
        var points = new List<(XYZ Internal, XYZ Shared, string Name)>();
        try { var io = InternalOrigin.Get(doc); points.Add((io.Position, io.SharedPosition, "internal origin")); } catch { /* optional */ }
        foreach (var (bp, name) in new[] { (BasePoint.GetProjectBasePoint(doc), "project base point"), (BasePoint.GetSurveyPoint(doc), "survey point") })
            if (bp != null) points.Add((bp.Position, bp.SharedPosition, name));

        // Revit's location transform maps shared to internal (its inverse gives shared coordinates); keep whichever
        // direction reproduces Revit's own shared positions of the base points, so the answer never rests on a guess.
        Shared Try(Transform t)
        {
            double err = points.Count == 0 ? 0 : points.Max(x => t.OfPoint(x.Internal).DistanceTo(x.Shared));
            return new Shared { ToShared = t, CheckError = err, CheckedAgainst = string.Join(", ", points.Select(x => x.Name)) };
        }
        var a = Try(total.Inverse);
        var b = Try(total);
        return a.CheckError <= b.CheckError ? a : b;
    }

    private static Obj Point(BasePoint bp, Transform report)
    {
        if (bp == null) return null;
        var o = new Obj
        {
            ["position"] = U.Xyz(report.OfPoint(bp.Position)),
            ["sharedPosition"] = U.Xyz(bp.SharedPosition),
        };
        try { o["clipped"] = bp.Clipped; } catch { /* optional */ }
        return o;
    }

    private static double PlanAngleDeg(XYZ v) => U.Deg(Math.Atan2(v.Y, v.X));

    private static Obj Report(Document doc, Transform report, out Shared shared)
    {
        shared = SharedOf(doc);
        var location = doc.ActiveProjectLocation;
        var o = new Obj { ["model"] = doc.Title };
        try { var io = InternalOrigin.Get(doc); o["internalOrigin"] = new Obj { ["position"] = U.Xyz(report.OfPoint(io.Position)), ["sharedPosition"] = U.Xyz(io.SharedPosition) }; }
        catch { /* optional */ }
        o["projectBasePoint"] = Point(BasePoint.GetProjectBasePoint(doc), report);
        o["surveyPoint"] = Point(BasePoint.GetSurveyPoint(doc), report);

        var position = location.GetProjectPosition(XYZ.Zero);
        o["activeLocation"] = new Obj
        {
            ["name"] = location.Name,
            ["angleToTrueNorthDeg"] = U.Deg(position.Angle),
            ["eastWest"] = U.Out(position.EastWest), ["northSouth"] = U.Out(position.NorthSouth), ["elevation"] = U.Out(position.Elevation),
        };
        o["locations"] = new FilteredElementCollector(doc).OfClass(typeof(ProjectLocation)).Cast<ProjectLocation>()
            .Select(l => l.Name).Distinct().OrderBy(n => n).ToList();
        o["sharedCoordinates"] = new Obj
        {
            ["note"] = "sharedPosition = Revit's shared (survey) coordinates. The internal -> shared transform was checked against "
                       + (shared.CheckedAgainst.Length > 0 ? shared.CheckedAgainst : "nothing") + ".",
            ["checkError"] = U.Out(shared.CheckError),
            ["rotationDeg"] = PlanAngleDeg(shared.ToShared.BasisX),
        };
        try
        {
            var site = doc.SiteLocation;
            var s = new Obj
            {
                ["placeName"] = site.PlaceName,
                ["latitudeDeg"] = U.Deg(site.Latitude), ["longitudeDeg"] = U.Deg(site.Longitude),
                ["timeZoneHours"] = site.TimeZone,
            };
            try { if (!string.IsNullOrEmpty(site.GeoCoordinateSystemId)) s["geoCoordinateSystem"] = site.GeoCoordinateSystemId; } catch { /* optional */ }
            o["site"] = s;
        }
        catch { /* optional */ }
        return o;
    }

    public static object GetCoordinates(Rc rc, Obj p)
    {
        var host = rc.Doc;
        var link = LinkScope.Of(host, p);
        if (link == null)
        {
            var o = Report(host, Transform.Identity, out _);
            o["units"] = U.Name;
            o["note"] = "position = Revit internal coordinates (what every other action uses). Shared coordinates are the survey system.";
            return o;
        }

        var result = Report(link.Doc, link.Tf, out var linkShared);
        var hostShared = SharedOf(host);
        // The same three points of the link, in the host's shared system and in the link's own: they agree when the link
        // was placed by shared coordinates (Acquire / Publish, or "By Shared Coordinates").
        var probes = new[] { XYZ.Zero, new XYZ(100, 0, 0), new XYZ(0, 100, 0) };
        var viaHost = probes.Select(q => hostShared.ToShared.OfPoint(link.Total.OfPoint(q))).ToArray();
        var own = probes.Select(q => linkShared.ToShared.OfPoint(q)).ToArray();
        var offset = viaHost[0] - own[0];
        double rot = PlanAngleDeg(viaHost[1] - viaHost[0]) - PlanAngleDeg(own[1] - own[0]);
        rot = ((rot + 540) % 360) - 180;
        double worst = viaHost.Zip(own, (x, y) => x.DistanceTo(y)).Max();
        bool agree = worst < U.Mm(1) && Math.Abs(rot) < 0.001;
        result["units"] = U.Name;
        result["link"] = link.Info();
        result["sharedCoordinatesAgree"] = agree;
        result["alignment"] = new Obj
        {
            ["offset"] = U.Xyz(offset), ["rotationDeg"] = Math.Round(rot, 6), ["largestDifference"] = U.Out(worst),
            ["note"] = agree
                ? "The link sits where its own shared coordinates say: host and link agree."
                : "The link does not sit where its shared coordinates say. offset / rotation = host shared minus link shared at the link's internal origin. "
                  + "Either the link was placed another way (origin to origin, centred, moved), or the two models' shared coordinates differ.",
        };
        result["note"] = link.HostCoordinates
            ? "position = the link's points in host internal coordinates (link transform applied); sharedPosition = the link's own shared coordinates."
            : "position = the link's own internal coordinates; sharedPosition = the link's own shared coordinates.";
        return result;
    }
}
