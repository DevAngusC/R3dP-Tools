using Rhino;
using Rhino.Geometry;

namespace PrintGear;

/// <summary>
/// Creates closed triangle/quad meshes for 60-degree, single-start printable threads.
/// The thread profile is represented analytically at every mesh vertex, which avoids
/// the fragile sweep and Boolean operations that are common failure points for threads.
/// </summary>
internal static class ThreadMeshBuilder
{
    private const int MinimumCircumferenceSegments = 96;
    private const int MaximumCircumferenceSegments = 240;
    private const int SegmentsPerPitch = 32;
    private const int MaximumLengthSegments = 1_600;

    public static Mesh? CreateMale(ThreadParameters parameters, out string error)
    {
        if (!parameters.IsValid(out error))
            return null;

        var (around, along) = GetResolution(parameters);
        var mesh = new Mesh();
        var outer = new int[along + 1, around];

        for (var row = 0; row <= along; row++)
        {
            var z = parameters.Length * row / along;
            for (var column = 0; column < around; column++)
            {
                var angle = 2.0 * Math.PI * column / around;
                var fullRadius = ThreadRadius(parameters, z, angle);
                var radius = parameters.MinorRadius +
                    (fullRadius - parameters.MinorRadius) * LeadInFactor(parameters, z);
                outer[row, column] = AddPolarVertex(mesh, radius, angle, z);
            }
        }

        AddOuterSurface(mesh, outer, along, around);

        var bottomCenter = mesh.Vertices.Add(0.0, 0.0, 0.0);
        var topCenter = mesh.Vertices.Add(0.0, 0.0, parameters.Length);
        for (var column = 0; column < around; column++)
        {
            var next = (column + 1) % around;
            // Bottom faces point down; top faces point up.
            mesh.Faces.AddFace(bottomCenter, outer[0, next], outer[0, column]);
            mesh.Faces.AddFace(topCenter, outer[along, column], outer[along, next]);
        }

        return Finish(mesh, out error);
    }

    public static Mesh? CreateFemale(ThreadParameters parameters, out string error)
    {
        if (!parameters.IsValid(out error))
            return null;

        var (around, along) = GetResolution(parameters);
        var mesh = new Mesh();
        var outer = new int[along + 1, around];
        var inner = new int[along + 1, around];

        for (var row = 0; row <= along; row++)
        {
            var z = parameters.Length * row / along;
            for (var column = 0; column < around; column++)
            {
                var angle = 2.0 * Math.PI * column / around;
                outer[row, column] = AddPolarVertex(mesh, parameters.FemaleOuterRadius, angle, z);
                inner[row, column] = AddPolarVertex(
                    mesh,
                    ThreadRadius(parameters, z, angle) + parameters.RadialClearance,
                    angle,
                    z);
            }
        }

        AddOuterSurface(mesh, outer, along, around);
        AddInnerSurface(mesh, inner, along, around);
        AddAnnularCaps(mesh, outer, inner, along, around);

        return Finish(mesh, out error);
    }

    private static (int Around, int Along) GetResolution(ThreadParameters parameters)
    {
        var around = Math.Clamp(
            (int)Math.Ceiling(parameters.NominalDiameter * Math.PI / Math.Max(parameters.Pitch * 0.20, 0.1)),
            MinimumCircumferenceSegments,
            MaximumCircumferenceSegments);
        var along = Math.Clamp(
            (int)Math.Ceiling(parameters.Length / parameters.Pitch * SegmentsPerPitch),
            SegmentsPerPitch,
            MaximumLengthSegments);
        return (around, along);
    }

    private static int AddPolarVertex(Mesh mesh, double radius, double angle, double z) =>
        mesh.Vertices.Add(radius * Math.Cos(angle), radius * Math.Sin(angle), z);

    private static void AddOuterSurface(Mesh mesh, int[,] vertices, int along, int around)
    {
        for (var row = 0; row < along; row++)
        for (var column = 0; column < around; column++)
        {
            var next = (column + 1) % around;
            mesh.Faces.AddFace(
                vertices[row, column],
                vertices[row, next],
                vertices[row + 1, next],
                vertices[row + 1, column]);
        }
    }

    private static void AddInnerSurface(Mesh mesh, int[,] vertices, int along, int around)
    {
        for (var row = 0; row < along; row++)
        for (var column = 0; column < around; column++)
        {
            var next = (column + 1) % around;
            // Reverse the winding so normals point into the threaded bore.
            mesh.Faces.AddFace(
                vertices[row, column],
                vertices[row + 1, column],
                vertices[row + 1, next],
                vertices[row, next]);
        }
    }

    private static void AddAnnularCaps(Mesh mesh, int[,] outer, int[,] inner, int along, int around)
    {
        for (var column = 0; column < around; column++)
        {
            var next = (column + 1) % around;
            // Bottom normal points down, top normal points up.
            mesh.Faces.AddFace(outer[0, column], inner[0, column], inner[0, next], outer[0, next]);
            mesh.Faces.AddFace(
                outer[along, column],
                outer[along, next],
                inner[along, next],
                inner[along, column]);
        }
    }

    private static double ThreadRadius(ThreadParameters parameters, double z, double angle)
    {
        var handedness = parameters.LeftHanded ? -1.0 : 1.0;
        var phase = z / parameters.Pitch - handedness * angle / (2.0 * Math.PI);
        phase -= Math.Floor(phase);

        // A truncated 60-degree profile: 1/8 pitch at the crest, 1/4 pitch at the root.
        // Crest is centred at phase 0.5, making the seam at phase 0 a root plateau.
        var distanceFromCrest = Math.Abs(phase - 0.5);
        const double crestHalfWidth = 0.0625;
        const double rootStart = 0.375;
        var ridge = distanceFromCrest <= crestHalfWidth
            ? 1.0
            : distanceFromCrest >= rootStart
                ? 0.0
                : (rootStart - distanceFromCrest) / (rootStart - crestHalfWidth);

        return parameters.MinorRadius + parameters.ThreadDepth * ridge;
    }

    private static double LeadInFactor(ThreadParameters parameters, double z)
    {
        // Taper only the raised ridge at both ends, leaving a solid cylindrical core.
        // This makes a printed male thread noticeably easier to start in its matching bore.
        var leadLength = Math.Min(parameters.Pitch * 0.65, parameters.Length * 0.25);
        if (leadLength <= RhinoMath.ZeroTolerance)
            return 1.0;

        var start = Math.Clamp(z / leadLength, 0.0, 1.0);
        var end = Math.Clamp((parameters.Length - z) / leadLength, 0.0, 1.0);
        return Math.Min(start, end);
    }

    private static Mesh? Finish(Mesh mesh, out string error)
    {
        mesh.Normals.ComputeNormals();
        mesh.Compact();

        if (!mesh.IsValid || !mesh.IsClosed)
        {
            error = "無法建立封閉且可列印的螺紋網格。";
            return null;
        }

        error = string.Empty;
        return mesh;
    }
}
