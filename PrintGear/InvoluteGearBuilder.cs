using Rhino;
using Rhino.Geometry;

namespace PrintGear;

/// <summary>Creates a closed involute spur-gear solid suitable for a watertight print export.</summary>
internal static class InvoluteGearBuilder
{
    private const int FlankSamples = 10;
    private const int ArcSamplesPerTooth = 5;

    public static Brep? Create(GearParameters parameters, double tolerance, out string error)
    {
        if (!parameters.IsValid(out error))
            return null;

        tolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);

        var profile = CreateOutline(parameters);
        if (profile is null || !profile.IsValid)
        {
            error = "無法建立有效的齒輪輪廓。";
            return null;
        }

        var gear = Extrusion.Create(profile, parameters.Thickness, true)?.ToBrep();
        if (gear is null || !gear.IsValid)
        {
            error = "無法將齒輪輪廓建立為實體。";
            return null;
        }

        if (parameters.EffectiveHubDiameter > 0.0 && parameters.HubThickness > RhinoMath.ZeroTolerance)
        {
            var hub = new Cylinder(
                new Circle(Plane.WorldXY, parameters.EffectiveHubDiameter / 2.0),
                parameters.HubThickness).ToBrep(true, true);
            var union = Brep.CreateBooleanUnion(new[] { gear, hub }, tolerance);
            if (union is { Length: 1 })
                gear = union[0];
        }

        if (parameters.EffectiveBoreDiameter > RhinoMath.ZeroTolerance)
        {
            var bore = new Cylinder(
                new Circle(Plane.WorldXY, parameters.EffectiveBoreDiameter / 2.0),
                parameters.Thickness + tolerance * 4.0)
                .ToBrep(true, true);
            bore.Translate(Vector3d.ZAxis * (-tolerance * 2.0));

            var difference = Brep.CreateBooleanDifference(gear, bore, tolerance);
            if (difference is not { Length: 1 })
            {
                error = "無法建立軸孔；請確認軸孔直徑未超過齒根直徑。";
                return null;
            }

            gear = difference[0];
        }

        if (!gear.IsValid)
        {
            error = "建立的齒輪實體無效。";
            return null;
        }

        error = string.Empty;
        return gear;
    }

    private static PolylineCurve? CreateOutline(GearParameters p)
    {
        var pressureAngle = RhinoMath.ToRadians(p.PressureAngleDegrees);
        var pitchRadius = p.PitchRadius;
        var baseRadius = pitchRadius * Math.Cos(pressureAngle);
        var rootRadius = p.RootRadius;
        var startRadius = Math.Max(rootRadius, baseRadius);
        var tipRadius = p.OutsideRadius;

        var halfToothAngle = (Math.PI / p.Teeth - (p.Backlash + 2.0 * p.FlankClearance) / pitchRadius) / 2.0;
        if (halfToothAngle <= RhinoMath.ZeroTolerance)
            return null;

        // An involute's polar angle increases as its radius increases.  Subtracting
        // that angle from the tooth centreline gives the standard profile: broad
        // at the root and narrower at the tip.  Adding it produces a reversed tooth.
        var baseRotation = halfToothAngle + InvoluteAngle(pitchRadius, baseRadius);
        var startFlankAngle = baseRotation - InvoluteAngle(startRadius, baseRadius);
        var tipFlankAngle = baseRotation - InvoluteAngle(tipRadius, baseRadius);
        if (startFlankAngle <= RhinoMath.ZeroTolerance || tipFlankAngle <= RhinoMath.ZeroTolerance)
            return null;

        var points = new List<Point3d>(p.Teeth * (FlankSamples * 2 + ArcSamplesPerTooth + 3));
        var toothStep = 2.0 * Math.PI / p.Teeth;

        for (var tooth = 0; tooth < p.Teeth; tooth++)
        {
            var centerAngle = tooth * toothStep;

            // Negative flank rises from the root to the tooth tip.
            AddPoint(points, Polar(rootRadius, centerAngle - startFlankAngle));
            if (rootRadius < startRadius - RhinoMath.ZeroTolerance)
                AddPoint(points, Polar(startRadius, centerAngle - baseRotation));

            for (var sample = 0; sample <= FlankSamples; sample++)
            {
                var radius = startRadius + (tipRadius - startRadius) * sample / FlankSamples;
                AddPoint(points, Polar(radius, centerAngle - (baseRotation - InvoluteAngle(radius, baseRadius))));
            }

            // The tip arc connects the two involute flanks.
            var tipAngle = tipFlankAngle;
            for (var sample = 1; sample <= ArcSamplesPerTooth; sample++)
            {
                var angle = centerAngle - tipAngle + 2.0 * tipAngle * sample / ArcSamplesPerTooth;
                AddPoint(points, Polar(tipRadius, angle));
            }

            // Positive flank returns from tip to root.
            for (var sample = FlankSamples - 1; sample >= 0; sample--)
            {
                var radius = startRadius + (tipRadius - startRadius) * sample / FlankSamples;
                AddPoint(points, Polar(radius, centerAngle + baseRotation - InvoluteAngle(radius, baseRadius)));
            }

            if (rootRadius < startRadius - RhinoMath.ZeroTolerance)
                AddPoint(points, Polar(rootRadius, centerAngle + baseRotation));

            // Root arc fills the gap before the next tooth.
            var nextNegativeRoot = centerAngle + toothStep - startFlankAngle;
            for (var sample = 1; sample <= ArcSamplesPerTooth; sample++)
            {
                var angle = centerAngle + startFlankAngle +
                    (nextNegativeRoot - (centerAngle + startFlankAngle)) * sample / ArcSamplesPerTooth;
                AddPoint(points, Polar(rootRadius, angle));
            }
        }

        if (points.Count < 4)
            return null;

        if (points[0].DistanceTo(points[^1]) > RhinoMath.ZeroTolerance)
            points.Add(points[0]);

        return new PolylineCurve(points);
    }

    private static double InvoluteAngle(double radius, double baseRadius)
    {
        var ratio = Math.Max(1.0, radius / baseRadius);
        var tangentLength = Math.Sqrt(ratio * ratio - 1.0);
        return tangentLength - Math.Acos(1.0 / ratio);
    }

    private static Point3d Polar(double radius, double angle) =>
        new(radius * Math.Cos(angle), radius * Math.Sin(angle), 0.0);

    private static void AddPoint(List<Point3d> points, Point3d point)
    {
        if (points.Count == 0 || points[^1].DistanceToSquared(point) > RhinoMath.ZeroTolerance * RhinoMath.ZeroTolerance)
            points.Add(point);
    }
}
