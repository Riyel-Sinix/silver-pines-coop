using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;

namespace SilverPinesCoop.Game
{
    /// <summary>
    /// Direct access to IL2CPP objects through the runtime API, for the game's plain data classes (the nested
    /// <c>PersistentData</c> classes of doors, pickups, interactables, ...). Only value fields are touched: primitives,
    /// enums and blittable structs, copied as raw bytes. Both clients run the same build, so the same class has the same
    /// field order and layout on each side. Reference fields (strings, lists, objects) are never read or written.
    /// </summary>
    internal static unsafe class Il2CppRaw
    {
        public const int ArrayDataOffset = 4 * 8;   // Il2CppArray: klass, monitor, bounds, max_length

        public readonly struct Field
        {
            public readonly int Offset;
            public readonly int Size;

            public Field(int offset, int size)
            {
                Offset = offset;
                Size = size;
            }
        }

        public sealed class Layout
        {
            public IntPtr Class;
            public string Image;       // e.g. "CarpenterScripts.dll"
            public string Namespace;   // of the outermost declaring type
            public string Name;        // "NewSideDoor/PersistentData" for nested classes
            public string OuterName;   // "NewSideDoor"
            public Field[] Fields;     // instance value fields in declaration order
        }

        private static readonly Dictionary<IntPtr, Layout> s_layouts = new();
        private static readonly Dictionary<string, IntPtr> s_classesByName = new();

        public static IntPtr ClassOf(IntPtr obj) => obj == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_object_get_class(obj);

        /// <summary>Layout of a reference class; null for value types and generic classes.</summary>
        public static Layout LayoutOf(IntPtr klass)
        {
            if (klass == IntPtr.Zero)
                return null;
            if (s_layouts.TryGetValue(klass, out var layout))
                return layout;
            layout = Build(klass);
            s_layouts[klass] = layout;
            return layout;
        }

        private static Layout Build(IntPtr klass)
        {
            if (IL2CPP.il2cpp_class_is_valuetype(klass))
                return null;
            var name = Str(IL2CPP.il2cpp_class_get_name(klass));
            if (string.IsNullOrEmpty(name) || name.Contains('`'))
                return null;
            var outer = klass;
            var fullName = name;
            for (var declaring = IL2CPP.il2cpp_class_get_declaring_type(klass); declaring != IntPtr.Zero;
                 declaring = IL2CPP.il2cpp_class_get_declaring_type(declaring))
            {
                outer = declaring;
                fullName = Str(IL2CPP.il2cpp_class_get_name(declaring)) + "/" + fullName;
            }
            // il2cpp_class_get_fields lists only the class's own fields: walk up to System.Object, base fields first
            var chain = new List<IntPtr>();
            for (var c = klass; c != IntPtr.Zero; c = IL2CPP.il2cpp_class_get_parent(c))
            {
                if (Str(IL2CPP.il2cpp_class_get_name(c)) == "Object" && Str(IL2CPP.il2cpp_class_get_namespace(c)) == "System")
                    break;
                chain.Add(c);
            }
            chain.Reverse();
            var fields = new List<Field>();
            foreach (var c in chain)
            {
                var iter = IntPtr.Zero;
                IntPtr field;
                while ((field = IL2CPP.il2cpp_class_get_fields(c, ref iter)) != IntPtr.Zero)
                {
                    if ((IL2CPP.il2cpp_field_get_flags(field) & 0x50) != 0)   // FIELD_ATTRIBUTE_STATIC | LITERAL
                        continue;
                    var size = ValueSize(IL2CPP.il2cpp_field_get_type(field));
                    if (size > 0)
                        fields.Add(new Field((int)IL2CPP.il2cpp_field_get_offset(field), size));
                }
            }
            return new Layout
            {
                Class = klass,
                Image = Str(IL2CPP.il2cpp_image_get_name(IL2CPP.il2cpp_class_get_image(klass))),
                Namespace = Str(IL2CPP.il2cpp_class_get_namespace(outer)),
                Name = fullName,
                OuterName = fullName.Split('/')[0],
                Fields = fields.ToArray(),
            };
        }

        /// <summary>Byte size of a value field we can copy, 0 for anything holding references.</summary>
        private static int ValueSize(IntPtr type)
        {
            switch (IL2CPP.il2cpp_type_get_type(type))
            {
                case 0x02: // BOOLEAN
                case 0x04: // I1
                case 0x05: // U1
                    return 1;
                case 0x03: // CHAR
                case 0x06: // I2
                case 0x07: // U2
                    return 2;
                case 0x08: // I4
                case 0x09: // U4
                case 0x0C: // R4
                    return 4;
                case 0x0A: // I8
                case 0x0B: // U8
                case 0x0D: // R8
                    return 8;
                case 0x11: // VALUETYPE: enums and structs (Vector3, Quaternion, ...)
                    var klass = IL2CPP.il2cpp_class_from_type(type);
                    if (klass == IntPtr.Zero || !(IL2CPP.il2cpp_class_is_enum(klass) || IL2CPP.il2cpp_class_is_blittable(klass)))
                        return 0;
                    uint align = 0;
                    var size = IL2CPP.il2cpp_class_value_size(klass, ref align);
                    return size is > 0 and <= 64 ? size : 0;
                default:
                    return 0;   // strings, objects, arrays, generic instances, pointers
            }
        }

        public static ulong Hash(IntPtr obj, Layout layout)
        {
            var h = 14695981039346656037ul;
            var p = (byte*)obj;
            foreach (var f in layout.Fields)
                for (var i = 0; i < f.Size; i++)
                {
                    h ^= p[f.Offset + i];
                    h *= 1099511628211ul;
                }
            return h;
        }

        /// <summary>All value fields of <paramref name="obj"/>, concatenated in layout order.</summary>
        public static byte[] Read(IntPtr obj, Layout layout)
        {
            var total = 0;
            foreach (var f in layout.Fields)
                total += f.Size;
            var bytes = new byte[total];
            var p = (byte*)obj;
            var n = 0;
            foreach (var f in layout.Fields)
                for (var i = 0; i < f.Size; i++)
                    bytes[n++] = p[f.Offset + i];
            return bytes;
        }

        /// <summary>Writes bytes produced by <see cref="Read"/> on the other client; false when the sizes disagree.</summary>
        public static bool Write(IntPtr obj, Layout layout, byte[] bytes)
        {
            var total = 0;
            foreach (var f in layout.Fields)
                total += f.Size;
            if (bytes == null || bytes.Length != total)
                return false;
            var p = (byte*)obj;
            var n = 0;
            foreach (var f in layout.Fields)
                for (var i = 0; i < f.Size; i++)
                    p[f.Offset + i] = bytes[n++];
            return true;
        }

        public static string Str(IntPtr utf8) => utf8 == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringUTF8(utf8);

        public static string ManagedString(IntPtr il2cppString) =>
            il2cppString == IntPtr.Zero ? null : IL2CPP.Il2CppStringToManaged(il2cppString);

        public static int FieldOffset(IntPtr klass, string name)
        {
            var field = IL2CPP.il2cpp_class_get_field_from_name(klass, name);
            return field == IntPtr.Zero ? -1 : (int)IL2CPP.il2cpp_field_get_offset(field);
        }

        public static IntPtr ReadPointer(IntPtr obj, int offset) => *(IntPtr*)((byte*)obj + offset);

        public static int ReadInt(IntPtr obj, int offset) => *(int*)((byte*)obj + offset);

        /// <summary>A class by image, namespace and "Outer/Inner" name, as produced by <see cref="Layout"/>.</summary>
        public static IntPtr FindClass(string image, string ns, string name)
        {
            var key = image + "|" + ns + "|" + name;
            if (s_classesByName.TryGetValue(key, out var klass))
                return klass;
            var parts = name.Split('/');
            klass = IL2CPP.GetIl2CppClass(image, ns ?? "", parts[0]);
            for (var i = 1; i < parts.Length && klass != IntPtr.Zero; i++)
            {
                var iter = IntPtr.Zero;
                IntPtr nested, found = IntPtr.Zero;
                while ((nested = IL2CPP.il2cpp_class_get_nested_types(klass, ref iter)) != IntPtr.Zero)
                    if (Str(IL2CPP.il2cpp_class_get_name(nested)) == parts[i])
                    {
                        found = nested;
                        break;
                    }
                klass = found;
            }
            s_classesByName[key] = klass;
            return klass;
        }

        /// <summary>A new instance with its parameterless constructor run; IntPtr.Zero when that fails.</summary>
        public static IntPtr New(IntPtr klass)
        {
            var obj = IL2CPP.il2cpp_object_new(klass);
            if (obj == IntPtr.Zero)
                return IntPtr.Zero;
            var ctor = IL2CPP.il2cpp_class_get_method_from_name(klass, ".ctor", 0);
            if (ctor != IntPtr.Zero)
            {
                var exception = IntPtr.Zero;
                IL2CPP.il2cpp_runtime_invoke(ctor, obj, (void**)IntPtr.Zero, ref exception);
                if (exception != IntPtr.Zero)
                    return IntPtr.Zero;
            }
            return obj;
        }

        /// <summary>Snapshot of a System.Collections.Generic.List&lt;T&gt; of references: its backing array and count.</summary>
        public static bool ListItems(IntPtr list, out IntPtr items, out int count)
        {
            items = IntPtr.Zero;
            count = 0;
            if (list == IntPtr.Zero)
                return false;
            var klass = ClassOf(list);
            if (!s_listOffsets.TryGetValue(klass, out var offsets))
            {
                offsets = (FieldOffset(klass, "_items"), FieldOffset(klass, "_size"));
                s_listOffsets[klass] = offsets;
            }
            if (offsets.Items < 0 || offsets.Size < 0)
                return false;
            items = ReadPointer(list, offsets.Items);
            count = ReadInt(list, offsets.Size);
            return items != IntPtr.Zero && count >= 0;
        }

        public static IntPtr ArrayElement(IntPtr array, int index) => *(IntPtr*)((byte*)array + ArrayDataOffset + index * 8);

        private static readonly Dictionary<IntPtr, (int Items, int Size)> s_listOffsets = new();
    }
}
