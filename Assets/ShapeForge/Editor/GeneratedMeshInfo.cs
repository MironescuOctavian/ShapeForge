using UnityEngine;

namespace ShapeForge
{
    internal sealed class GeneratedMeshInfo : ScriptableObject
    {
        public Mesh original;
        public SyncChange syncChange;
        public string bodyPath;
        public string diagnostic;
        public GeneratedShape[] shapes;

        // Assets saved before per-shape records existed could only hold this one shape.
        private const string LegacyShape = "(No bra)Breasts_Small";

        internal GeneratedShape[] Entries => shapes != null && shapes.Length > 0 ? shapes : new[]
        {
            new GeneratedShape { name = LegacyShape, syncChange = syncChange, bodyPath = bodyPath }
        };
    }

    [System.Serializable]
    internal struct GeneratedShape
    {
        public string name;
        public bool reverse;
        public SyncChange syncChange;
        public string bodyPath;
    }

    internal enum SyncChange
    {
        None,
        AddedBinding,
        AddedComponent
    }
}
