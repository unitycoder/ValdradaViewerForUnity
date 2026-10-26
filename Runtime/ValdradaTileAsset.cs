using UnityEngine;

namespace UnityCoder.ValdradaViewer
{
    // Asset container for imported Valdrada tile meshes and materials.
    public sealed class ValdradaTileAsset : ScriptableObject
    {
        public string sourceFile;
        public int tileX;
        public int tileZ;
        public int buildingCount;
        public bool flippedZ;
    }
}
