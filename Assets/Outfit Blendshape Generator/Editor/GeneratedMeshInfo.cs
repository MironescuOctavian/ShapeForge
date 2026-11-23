using UnityEngine;

namespace MilltinaBreastGenerator
{
    internal sealed class GeneratedMeshInfo : ScriptableObject
    {
        public Mesh original;
        public SyncChange syncChange;
        public string bodyPath;
        public string diagnostic;
        public GeneratedShape[] shapes;

        internal GeneratedShape[] Entries => shapes != null && shapes.Length > 0 ? shapes : new[]
        {
            new GeneratedShape { name = ShapeTransfer.Shape, syncChange = syncChange, bodyPath = bodyPath }
        };
    }

    [System.Serializable]
    internal struct GeneratedShape
    {
        public string name;
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
