using UnityEngine;

namespace ShapeForge
{
    internal sealed class GeneratedMeshInfo : ScriptableObject
    {
        public Mesh original;
        public string diagnostic;
        public GeneratedShape[] shapes;

        internal GeneratedShape[] Entries => shapes ?? System.Array.Empty<GeneratedShape>();
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
