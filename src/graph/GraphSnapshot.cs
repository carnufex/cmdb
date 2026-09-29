using System.Runtime.InteropServices;
using System.Text;

namespace Cmdb.Graph;

/// <summary>
/// The graph's arrays written as they are in memory, so a pod starts in seconds instead of re-reading the database.
/// The header carries a format version and the data version the graph was built from.
/// </summary>
public static class GraphSnapshot
{
    private const uint Magic = 0x48505247; // "GRPH"
    private const int Format = 2;

    public static void Write(Graph graph, Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Format);
        w.Write(graph.Version);
        Array(w, graph.TerminalIds);
        Array(w, graph.TerminalKinds);
        Array(w, graph.TerminalOwners);
        Array(w, graph.EdgeStart);
        Array(w, graph.EdgeTargets);
        Array(w, graph.EdgeKinds);
        Array(w, graph.EdgeLifecycles);
        Array(w, graph.EquipmentIds);
        Array(w, graph.EquipmentSites);
        Array(w, graph.SiteIds);
        Array(w, graph.ConductorIds);
        Array(w, graph.ConductorCables);
        Array(w, graph.CableIds);
        Array(w, graph.CableLifecycles);
        Array(w, graph.CircuitIds);
        Array(w, graph.CircuitLayers);
        Array(w, graph.HopStart);
        Array(w, graph.HopNodes);
        Array(w, graph.NodeCircuitStart);
        Array(w, graph.NodeCircuits);
        Array(w, graph.DependentStart);
        Array(w, graph.Dependents);
        Array(w, graph.CircuitServiceStart);
        Array(w, graph.CircuitServices);
        Array(w, graph.ServiceIds);
        Array(w, graph.ServiceCircuitStart);
        Array(w, graph.ServiceCircuitList);
        Array(w, graph.CarrierStart);
        Array(w, graph.Carriers);
    }

    /// <summary>Reads a snapshot, or returns null when the file is from another format or data version.</summary>
    public static Graph? Read(Stream stream, string? expectedVersion = null)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic || r.ReadInt32() != Format)
        {
            return null;
        }
        var version = r.ReadString();
        if (expectedVersion is not null && version != expectedVersion)
        {
            return null;
        }
        return new Graph
        {
            Version = version,
            TerminalIds = Array<long>(r),
            TerminalKinds = Array<TerminalKind>(r),
            TerminalOwners = Array<int>(r),
            EdgeStart = Array<int>(r),
            EdgeTargets = Array<int>(r),
            EdgeKinds = Array<EdgeKind>(r),
            EdgeLifecycles = Array<Lifecycle>(r),
            EquipmentIds = Array<long>(r),
            EquipmentSites = Array<int>(r),
            SiteIds = Array<long>(r),
            ConductorIds = Array<long>(r),
            ConductorCables = Array<int>(r),
            CableIds = Array<long>(r),
            CableLifecycles = Array<Lifecycle>(r),
            CircuitIds = Array<long>(r),
            CircuitLayers = Array<CircuitLayer>(r),
            HopStart = Array<int>(r),
            HopNodes = Array<int>(r),
            NodeCircuitStart = Array<int>(r),
            NodeCircuits = Array<int>(r),
            DependentStart = Array<int>(r),
            Dependents = Array<int>(r),
            CircuitServiceStart = Array<int>(r),
            CircuitServices = Array<int>(r),
            ServiceIds = Array<long>(r),
            ServiceCircuitStart = Array<int>(r),
            ServiceCircuitList = Array<int>(r),
            CarrierStart = Array<int>(r),
            Carriers = Array<int>(r),
        };
    }

    public static void WriteFile(Graph graph, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
        {
            Write(graph, file);
        }
        // Replace atomically so a pod never reads a half-written snapshot.
        File.Move(temp, path, overwrite: true);
    }

    public static Graph? ReadFile(string path, string? expectedVersion)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        try
        {
            return Read(file, expectedVersion);
        }
        catch (EndOfStreamException)
        {
            return null;
        }
    }

    private static void Array<T>(BinaryWriter w, T[] values)
        where T : unmanaged
    {
        w.Write(values.Length);
        w.Write(MemoryMarshal.AsBytes(values.AsSpan()));
    }

    private static T[] Array<T>(BinaryReader r)
        where T : unmanaged
    {
        var values = new T[r.ReadInt32()];
        r.BaseStream.ReadExactly(MemoryMarshal.AsBytes(values.AsSpan()));
        return values;
    }
}
