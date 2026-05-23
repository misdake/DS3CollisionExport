using System;
using System.Reflection;

namespace hkx_reflect
{
    class Program
    {
        static void DumpType(string fullName)
        {
            var t = Type.GetType(fullName);
            if (t == null) { Console.WriteLine($"Type not found: {fullName}"); return; }
            Console.WriteLine($"=== {t.FullName} ===");
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                Console.WriteLine($"F {f.FieldType.FullName} {f.Name}");
            Console.WriteLine();
        }
        static void Main(string[] args)
        {
            DumpType("HKX2.hknpCompressedMeshShapeTree, HKX2");
            DumpType("HKX2.hkcdStaticMeshTreeBaseSectionSharedVertices, HKX2");
            DumpType("HKX2.hkcdStaticMeshTreeBaseSectionPrimitives, HKX2");
            DumpType("HKX2.hkAabb, HKX2");
        }
    }
}
