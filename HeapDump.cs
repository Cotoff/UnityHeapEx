using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityHeapEx
{
    public class HeapDump
    {
        [MenuItem( "Tools/Memory/HeapDump" )]
        public static void DoStuff()
        {
            var h = new HeapDump();
            h.DumpToXml();
        }

        private readonly HashSet<object> seenObjects = new HashSet<object>();
        private StreamWriter writer;

        // when true, types w/o any static field (and skipped types) are removed from output
        public static bool SkipEmptyTypes = true;

        /* TODO
         * ignore cached delegates for lambdas(?)
         * better type names
         * deal with arrays of arrays
         * maybe special code for delegates? coroutines?
         */
		
		/// <summary>
		/// Collect all roots, i.e. static fields in all classes and all scripts; then dump all object 
		/// hierarchy reachable from these roots to file
		/// </summary>
        public void DumpToXml()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
			// assembles contain all referenced system assemblies, as well as UnityEngine, UnityEditor and editor
			// code. To make root list smaller, we only select main game assembly from the list, although it might
			// concievably miss some statics
            var gameAssembly = assemblies.Single( a => a.FullName.Contains( "Assembly-CSharp," ) );
            var allTypes = gameAssembly.GetTypes();

            var allScripts = UnityEngine.Object.FindObjectsOfType( typeof( MonoBehaviour ) );

            seenObjects.Clear(); // used to prevent going through same object twice
            using( writer = new StreamWriter( "heapdump.xml" ) )
            {
                writer.WriteLine( "<?xml version=\"1.0\" encoding=\"utf-8\"?>" );
                int totalSize = 0;
                writer.WriteLine( "<statics>" );
				// enumerate all static fields
                foreach( var type in allTypes )
                {
                    bool tagWritten = false;
                    if(!SkipEmptyTypes)
                        writer.WriteLine( "  <type name=\"{0}\">", SecurityElement.Escape( type.GetFormattedName() ) );
                    if(type.IsEnum)
                    {
						// enums don't hold anything but their constants
                        if( !SkipEmptyTypes )
                            writer.WriteLine( "<ignored reason=\"IsEnum\"/>" );
                    }
                    else if(type.IsGenericType)
                    {
						// generic types are ignored, because we can't access static fields unless we
						// know actual type parameters of the class containing generics, and we have no way
						// of knowing these - they depend on what concrete type were ever instantiated.
						// This may miss a lot of stuff if generics are heavily used.
                        if( !SkipEmptyTypes )
                            writer.WriteLine( "<ignored reason=\"IsGenericType\"/>" );
                    }
                    else
                    {
                        foreach( var fieldInfo in type.GetFields( BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic ) )
                        {
                            try
                            {
                                if(SkipEmptyTypes && !tagWritten)
                                {
                                    writer.WriteLine( "  <type name=\"{0}\">", SecurityElement.Escape( type.GetFormattedName() ) );
                                    tagWritten = true;
                                }

                                int size = ReportField( null, "    ", fieldInfo );
                                totalSize += size;
                            }
                            catch( Exception ex )
                            {
                                writer.WriteLine( "Exception: " + ex.Message + " on " + fieldInfo.FieldType.GetFormattedName() + " " +
                                                  fieldInfo.Name );
                            }
                        }
                    }
                    if( !SkipEmptyTypes || tagWritten )
                        writer.WriteLine( "  </type>" );
                }
                writer.WriteLine( "</statics>" );
				
				// enumerate all MonoBehaviours - that is, all user scripts on all existing objects.
				// TODO this maybe misses objects with active==false.
                writer.WriteLine( "<scripts>" );
                foreach( MonoBehaviour mb in allScripts )
                {
                    writer.WriteLine( "  <object type=\"{0}\" name=\"{1}\">", SecurityElement.Escape( mb.GetType().GetFormattedName() ), SecurityElement.Escape( mb.name ) );
                    var type = mb.GetType();
                    foreach( var fieldInfo in type.EnumerateAllFields() )
                    {
                        try
                        {
                            int size = ReportField( mb, "    ", fieldInfo );
                            totalSize += size;
                        }
                        catch( Exception ex )
                        {
                            writer.WriteLine( "Exception: " + ex.Message + " on " + fieldInfo.FieldType.GetFormattedName() + " " +
                                              fieldInfo.Name );
                        }
                    }
                    writer.WriteLine( "  </object>" );
                }
                writer.WriteLine( "</scripts>" );
                //                writer.WriteLine( "Total size: " + totalSize );
            }
            Debug.Log( "OK" );
        }
		
		/// <summary>
		/// Works through all fields of an object, dumpoing them into xml
		/// </summary>
        public int GatherFromRootRecursively(object root, string depth)
        {
            var seen = seenObjects.Contains( root );

            if( root is Object )
            {
                var uo = root as Object;
                WriteUnityObjectData( depth, uo, seen );
            }

            if( seen )
            {
                if(!(root is Object))
                {
					// XXX maybe add some object index so that this is traceable to original object dump
					// earlier in xml?
                    writer.WriteLine( depth + "<seen/>" );
                    
                }
                return 0;
            }

            seenObjects.Add( root );

            var type = root.GetType();
            var fields = type.EnumerateAllFields();
            var res = 0;
            foreach( var fieldInfo in fields )
            {
                try
                {
                    res += ReportField( root, depth, fieldInfo );
                }
                catch( Exception ex )
                {
                    writer.WriteLine( "Exception: " + ex.Message + " on " + fieldInfo.FieldType.GetFormattedName() + " " + fieldInfo.Name );
                }
            }
            return res;
        }

        private void WriteUnityObjectData( string depth, Object uo, bool seen )
        {
			// shows some additional info on UnityObjects
            writer.WriteLine( depth + "<unityObject type=\"{0}\" name=\"{1}\" seen=\"{2}\"/>",
                              SecurityElement.Escape( uo.GetType().GetFormattedName() ),
                              SecurityElement.Escape( uo ? uo.name : "--missing reference--" ),
                              seen );
            // todo we can show referenced assets for renderers, materials, audiosources etc
        }
		
		/// <summary>
		/// Dumps info on the field in xml. Provides some rough estimate on size taken by its contents,
		/// and recursively enumerates fields if this one contains an object reference.
		/// </summary>
		/// <returns>
		/// Rough estimate of memory taken by field and its contents
		/// </returns>
        private int ReportField(object root, string depth, FieldInfo fieldInfo)
        {
            var v = fieldInfo.GetValue( root );
            int res = 0;
            var ftype = v==null?null:v.GetType();

            writer.WriteLine( depth + "<field type=\"{0}\" name=\"{1}\" runtimetype=\"{2}\">",
                SecurityElement.Escape( fieldInfo.FieldType.GetFormattedName() ), 
                SecurityElement.Escape( fieldInfo.Name ),
                SecurityElement.Escape( v==null?"-null-":ftype.GetFormattedName())
                );

            if(v==null)
            {
                res += IntPtr.Size;
            }
            else if( ftype.IsArray )
            {
				// arrays have special treatment b/c we have to work on every array element
				// just like a single value.
				// TODO refactor this so that arry item and non-array field value share code
                var val = v as Array;
                res += IntPtr.Size; // reference size
                if( val != null && !seenObjects.Contains( val ))
                {
                    seenObjects.Add( val );
                    var length = GetTotalLength( val );
                    writer.WriteLine( depth + "  <array length=\"{0}\"/>", length );
                    var eltype = ftype.GetElementType();
                    if( eltype.IsValueType )
                    {
                        if( eltype.IsEnum )
                            eltype = Enum.GetUnderlyingType( eltype );
                        try
                        {
                            res += Marshal.SizeOf( eltype ) * length;
                        }
                        catch( Exception )
                        {
                            writer.WriteLine( depth + "  <error msg=\"Marshal.SizeOf() failed\"/>" );
                        }
                    }
                    else if( eltype == typeof( string ) )
                    {
                        // special case
                        res += IntPtr.Size * length; // array itself

                        foreach( string item in val )
                        {
                            if( item != null )
                            {
                                writer.WriteLine( depth + "  <string length=\"{0}\"/>", item.Length );
                                if(!seenObjects.Contains( val ))
                                {
                                    seenObjects.Add( val );
                                    res += sizeof( char ) * item.Length + sizeof( int );
                                }
                            }
                        }
                    }

                    else
                    {
                        res += IntPtr.Size * length; // array itself
                        foreach( var item in val )
                        {
                            if( item != null )
                            {
                                writer.WriteLine( depth + "  <item type=\"{0}\">", SecurityElement.Escape( item.GetType().GetFormattedName() ) );
                                res += GatherFromRootRecursively( item, depth + "    " );
                                writer.WriteLine( depth + "  </item>");
                            }
                        }
                    }
                }
                else
                {
                    writer.WriteLine( depth + "  <null/>" );
                }
            }
            else if( ftype.IsValueType )
            {
                if( ftype.IsPrimitive )
                {
                    var val = fieldInfo.GetValue( root );
                    res += Marshal.SizeOf( ftype );
                    writer.WriteLine( depth + "  <value value=\"{0}\"/>", val );
                }
                else if( ftype.IsEnum )
                {
                    var val = fieldInfo.GetValue( root );
                    res += Marshal.SizeOf( Enum.GetUnderlyingType( ftype ) );
                    writer.WriteLine( depth + "  <value value=\"{0}\"/>", val );
                }
                else
                {
					// this is a struct. This code assumes that all structs contain only primitive types,
					// which is very strong. Structs that contain references will break, and fail to traverse these
					// references properly
                    int s = 0;
                    try
                    {
                        s = Marshal.SizeOf( ftype );
                    }
                    catch( Exception )
                    {
                        // this breaks if struct has a reference member. We should probably never have such structs, but we'll see...
                        writer.WriteLine( depth + "  <error msg=\"Marshal.SizeOf() failed\"/>" );
                    }
                    writer.WriteLine( depth + "  <struct size=\"{0}\"/>", s );
                    res += s;
                }
            }
            else if( ftype == typeof( string ) )
            {
                // special case
                res += IntPtr.Size; // reference size
                var val = fieldInfo.GetValue( root ) as string;
                if( val != null )
                {
                    writer.WriteLine( depth + "  <string length=\"{0}\"/>", val.Length );
                    if(!seenObjects.Contains( val ))
                    {
                        seenObjects.Add( val );
                        res += sizeof( char ) * val.Length + sizeof( int );
                    }
                }
                else
                    writer.WriteLine( depth + "  <null/>" );
            }
            else
            {
                // this is a reference 
                var classVal = fieldInfo.GetValue( root );
                res += IntPtr.Size; // reference size
                if( classVal != null )
                {
                    res += GatherFromRootRecursively( classVal, depth + "  " );
                }
                else
                {
                    writer.WriteLine( depth + "  <null/>" );
                }
            }
            writer.WriteLine( depth + "  <total size=\"{0}\"/>", res );
            writer.WriteLine( depth + "</field>" );

            return res;
        }

        private int GetTotalLength(Array val)
        {
            var rank = val.Rank;
            if( rank == 1 )
                return val.Length;

            var l = 1;
            while( rank > 0 )
            {
                l *= val.GetLength( rank - 1 );
                rank--;
            }
            return l;
        }
    }
}