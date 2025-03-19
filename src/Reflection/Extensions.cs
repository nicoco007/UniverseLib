﻿using System;
using System.Collections.Generic;
using System.Reflection;
using UniverseLib.Utility;

namespace UniverseLib
{
    public static class ReflectionExtensions
    {
        /// <summary>
        /// Get the true underlying Type of the provided object.
        /// </summary>
        public static Type GetActualType(this object obj)
            => ReflectionUtility.Instance.Internal_GetActualType(obj);

        /// <summary>
        /// Attempt to cast the provided object to it's true underlying Type.
        /// </summary>
        public static object TryCast(this object obj)
            => ReflectionUtility.Instance.Internal_TryCast(obj, ReflectionUtility.Instance.Internal_GetActualType(obj));

        /// <summary>
        /// Attempt to cast the provided object to the provided Type <paramref name="castTo"/>.
        /// </summary>
        public static object TryCast(this object obj, Type castTo)
            => ReflectionUtility.Instance.Internal_TryCast(obj, castTo);

        /// <summary>
        /// Attempt to cast the provided object to Type <typeparamref name="T"/>.
        /// </summary>
        public static T TryCast<T>(this object obj)
        {
            try
            {
                return (T)ReflectionUtility.Instance.Internal_TryCast(obj, typeof(T));
            }
            catch
            {
                return default;
            }
        }

        // ------- Misc extensions --------

        public static IEnumerable<Type> TryGetTypes(this Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                Universe.LogWarning($"Failed to get types from assembly {asm.FullName}\n{ex}");
                return ReflectionUtility.TryExtractTypesFromException(ex);
            }
            catch (Exception ex) // It was some other exception, try use GetExportedTypes
            {
                Universe.LogWarning($"Failed to get types from assembly {asm.FullName}\n{ex}");

                try
                {
                    return asm.GetExportedTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    Universe.LogWarning($"Failed to get exported types from assembly {asm.FullName}\n{e}");
                    return ReflectionUtility.TryExtractTypesFromException(e);
                }
                catch
                {
                    return ArgumentUtility.EmptyTypes;
                }
            }
        }

        /// <summary>
        /// Check if the two objects are reference-equal, including checking for UnityEngine.Object-equality and Il2CppSystem.Object-equality.
        /// </summary>
        public static bool ReferenceEqual(this object objA, object objB)
        {
            if (object.ReferenceEquals(objA, objB))
                return true;

            if (objA is UnityEngine.Object unityA && objB is UnityEngine.Object unityB)
            {
                if (unityA && unityB && unityA.m_CachedPtr == unityB.m_CachedPtr)
                    return true;
            }

#if IL2CPP
            if (objA is Il2CppSystem.Object cppA && objB is Il2CppSystem.Object cppB
                && cppA.Pointer == cppB.Pointer)
                return true;
#endif

            return false;
        }

        /// <summary>
        /// Helper to display a simple "{ExceptionType}: {Message}" of the exception, and optionally use the inner-most exception.
        /// </summary>
        public static string ReflectionExToString(this Exception e, bool innerMost = true)
        {
            if (e == null)
                return "The exception was null.";

            if (innerMost)
                e = e.GetInnerMostException();

            return $"{e.GetType()}: {e.Message}";
        }

        /// <summary>
        /// Get the inner-most exception from the provided exception, if there are any. This is recursive.
        /// </summary>
        /// <param name="e"></param>
        /// <returns></returns>
        public static Exception GetInnerMostException(this Exception e)
        {
            while (e != null)
            {
                if (e.InnerException == null)
                    break;
#if IL2CPP
                if (e.InnerException is System.Runtime.CompilerServices.RuntimeWrappedException)
                    break;
#endif
                e = e.InnerException;
            }

            return e;
        }
    }
}
