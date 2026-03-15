using System;
using System.Linq;
using Mono.Cecil;

namespace DllAnalyzer
{
    class Program
    {
        static void Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : @"D:\Steam\steamapps\common\MIMESIS\MIMESIS_Data\Managed\Assembly-CSharp.dll";
            var ad = AssemblyDefinition.ReadAssembly(path);
            var types = ad.MainModule.Types.Where(t => (t.IsClass || t.IsValueType) && !string.IsNullOrEmpty(t.Namespace) && !t.Namespace.StartsWith("<")).ToList();

            Console.WriteLine("=== Types with Vector3 fields ===");
            foreach (var type in types)
            {
                foreach (var f in type.Fields)
                {
                    string fn = f.FieldType.FullName ?? "";
                    if (fn.Contains("Vector3"))
                        Console.WriteLine($"{type.FullName} | {f.Name} | {fn}");
                }
            }
            Console.WriteLine("\n=== Types named *Player* / *Character* / *Controller* / *Camera* (with fields) ===");
            string[] kw = { "Player", "Character", "Controller", "Camera", "GameManager", "Local" };
            foreach (var type in types)
            {
                if (!kw.Any(k => type.Name.Contains(k))) continue;
                Console.WriteLine($"\n{type.FullName}");
                foreach (var f in type.Fields)
                    Console.WriteLine($"  .{f.Name} : {f.FieldType.FullName}");
            }

            // マップ・ダンジョン・レベル・ルーム・DunGen 関連の型（ミニマップ用）
            Console.WriteLine("\n========== MAP / DUNGEON / LEVEL / ROOM (for Minimap) ==========");
            string[] mapKw = { "Map", "Dungeon", "Level", "Room", "Tile", "Grid", "Layout", "DunGen", "Floor", "Corridor", "Chunk", "Cell", "NavMesh", "Bounds", "Tilemap", "Procedural", "Generator", "Spawn" };
            foreach (var type in types)
            {
                if (!mapKw.Any(k => type.Name.Contains(k))) continue;
                Console.WriteLine($"\n--- {type.FullName} ---");
                foreach (var f in type.Fields)
                {
                    string fn = f.FieldType.FullName ?? "";
                    bool interesting = fn.Contains("Vector3") || fn.Contains("Bounds") || fn.Contains("List") || fn.Contains("[]") || fn.Contains("Room") || fn.Contains("Tile") || fn.Contains("Transform");
                    if (interesting || type.Namespace != null && type.Namespace.Contains("DunGen"))
                        Console.WriteLine($"  [F] {f.Name} : {fn}");
                }
                foreach (var m in type.Methods.Where(m => (m.IsPublic || m.IsAssembly) && !m.IsConstructor && !m.Name.StartsWith("set_")))
                {
                    if (m.Name.StartsWith("get_")) continue;
                    string sig = m.Name + "(" + string.Join(", ", m.Parameters.Select(p => p.ParameterType.FullName)) + ")";
                    string ret = m.ReturnType.FullName ?? "";
                    bool interesting = ret.Contains("Room") || ret.Contains("Tile") || ret.Contains("Bounds") || ret.Contains("List") || ret.Contains("IEnumerable") || m.Name.Contains("Room") || m.Name.Contains("Tile") || m.Name.Contains("Get") || m.Name.Contains("Bounds");
                    if (interesting || (type.Namespace != null && type.Namespace.Contains("DunGen")))
                        Console.WriteLine($"  [M] {sig} -> {ret}");
                }
            }

            // DunGen 名前空間の型をすべて列挙（フィールド・プロパティ・メソッド）
            Console.WriteLine("\n========== DunGen namespace (all types, full members) ==========");
            foreach (var type in types.Where(t => t.Namespace != null && t.Namespace.StartsWith("DunGen")))
            {
                Console.WriteLine($"\n--- {type.FullName} ---");
                foreach (var f in type.Fields)
                    Console.WriteLine($"  [F] {f.Name} : {f.FieldType.FullName}");
                foreach (var prop in type.Properties)
                    Console.WriteLine($"  [P] {prop.Name} : {prop.PropertyType.FullName}");
                foreach (var m in type.Methods.Where(m => (m.IsPublic || m.IsAssembly) && !m.IsConstructor && !m.Name.StartsWith("set_") && !m.Name.StartsWith("get_")))
                    Console.WriteLine($"  [M] {m.Name}({string.Join(", ", m.Parameters.Select(p => p.ParameterType.Name))}) -> {m.ReturnType.FullName}");
            }
        }
    }
}
