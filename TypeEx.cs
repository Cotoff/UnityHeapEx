using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;

namespace UnityHeapEx
{
    public static class TypeEx
    {
		/// <summary>
		/// Enumerates all fields of a type, including those defined in base types.
		/// </summary>
        public static IEnumerable<FieldInfo> EnumerateAllFields(this Type type)
        {
            var @base = type.BaseType;
            if (@base != null)
                foreach (var field in EnumerateAllFields(@base))
                {
                    yield return field;
                }
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return field;
            }
        }
		
		/// <summary>
		/// Formats type name, including generic type arguments if any. Also changes angle brackets
		/// to parenteses so that type is more readable in xml
		/// </summary>
        public static string GetFormattedName(this Type type)
        {
            var name = type.Name;
            name = name.Replace( "<", "(" ).Replace( ">", ")" ); // makes XML easier to read
            if (type.IsGenericType)
            {
                name += "(" + String.Join(", ", type.GetGenericArguments().Select<Type, String>(GetFormattedName).ToArray()) +
                        ")";
            }
            if (type.IsNested)
            {
                name = GetFormattedName(type.DeclaringType) + "." + name;
            }

            return name;
        }
    }
}