using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using RuntimeBinder = Microsoft.CSharp.RuntimeBinder;

namespace Python.Runtime
{
    /// <summary>
    /// Managed class that provides the implementation for reflected dynamic types.
    /// This has the usage as ClassObject but for the dynamic types special case,
    /// that is, classes implementing IDynamicMetaObjectProvider interface.
    /// This adds support for using dynamic properties of the C# object.
    /// </summary>
    [Serializable]
    internal class DynamicClassObject : ClassObject
    {
        internal DynamicClassObject(Type tp) : base(tp)
        {
        }

        private static Dictionary<ValueTuple<Type, string>, CallSite<Func<CallSite, object, object>>> _getAttrCallSites = new();
        private static Dictionary<ValueTuple<Type, string>, CallSite<Func<CallSite, object, object, object>>> _setAttrCallSites = new();

        private static CallSite<Func<CallSite, object, object>> GetAttrCallSite(string name, Type objectType)
        {
            var key = ValueTuple.Create(objectType, name);
            if (!_getAttrCallSites.TryGetValue(key, out var callSite))
            {
                var binder = RuntimeBinder.Binder.GetMember(
                    RuntimeBinder.CSharpBinderFlags.None,
                    name,
                    objectType,
                    new[] { RuntimeBinder.CSharpArgumentInfo.Create(RuntimeBinder.CSharpArgumentInfoFlags.None, null) });
                callSite = CallSite<Func<CallSite, object, object>>.Create(binder);
                _getAttrCallSites[key] = callSite;
            }

            return callSite;
        }

        private static CallSite<Func<CallSite, object, object, object>> SetAttrCallSite(string name, Type objectType)
        {
            var key = ValueTuple.Create(objectType, name);
            if (!_setAttrCallSites.TryGetValue(key, out var callSite))
            {
                var binder = RuntimeBinder.Binder.SetMember(
                    RuntimeBinder.CSharpBinderFlags.None,
                    name,
                    objectType,
                    new[]
                    {
                        RuntimeBinder.CSharpArgumentInfo.Create(RuntimeBinder.CSharpArgumentInfoFlags.None, null),
                        RuntimeBinder.CSharpArgumentInfo.Create(RuntimeBinder.CSharpArgumentInfoFlags.None, null)
                    });
                callSite = CallSite<Func<CallSite, object, object, object>>.Create(binder);
                _setAttrCallSites[key] = callSite;
            }
            return callSite;
        }

        /// <summary>
        /// Type __getattro__ implementation.
        /// </summary>
        public static NewReference tp_getattro(BorrowedReference ob, BorrowedReference key)
        {
            if (!TryGetNonDynamicMember(ob, key, out var result))
            {
                var clrObj = (CLRObject)GetManagedObject(ob)!;

                var name = Runtime.GetManagedString(key);
                var clrObjectType = clrObj.inst.GetType();
                var callSite = GetAttrCallSite(name, clrObjectType);

                try
                {
                    var res = callSite.Target(callSite, clrObj.inst);
                    Exceptions.Clear();
                    result = Converter.ToPython(res);
                }
                catch (RuntimeBinder.RuntimeBinderException)
                {
                    // Do nothing, AttributeError was already raised in Python side and it was not cleared.
                }
                // Catch C# exceptions and raise them as Python exceptions.
                catch (Exception exception)
                {
                    Exceptions.Clear();
                    // tp_getattro should call PyObject_GenericGetAttr (which we already did)
                    // which must throw AttributeError if the attribute is not found (see https://docs.python.org/3/c-api/object.html#c.PyObject_GenericGetAttr)
                    // So if we are throwing anything, it must be AttributeError.
                    // e.g hasattr uses this method to check if the attribute exists. If we throw anything other than AttributeError,
                    // hasattr will throw instead of catching and returning False.
                    Exceptions.SetError(Exceptions.AttributeError, exception.Message);
                    return default;
                }
            }

            return result;
        }

        /// <summary>
        /// Type __setattr__ implementation.
        /// </summary>
        public static int tp_setattro(BorrowedReference ob, BorrowedReference key, BorrowedReference val)
        {
            if (TryGetNonDynamicMember(ob, key, out _, clearExceptions: true))
            {
                return Runtime.PyObject_GenericSetAttr(ob, key, val);
            }

            var clrObj = (CLRObject)GetManagedObject(ob)!;
            var name = Runtime.GetManagedString(key);
            var callsite = SetAttrCallSite(name, clrObj.inst.GetType());
            try
            {
                callsite.Target(callsite, clrObj.inst, PyObject.FromNullableReference(val));
            }
            // Catch C# exceptions and raise them as Python exceptions.
            catch (Exception exception)
            {
                // tp_setattro should call PyObject_GenericSetAttr (which we already did)
                // which must throw AttributeError on failure and return -1 (see https://docs.python.org/3/c-api/object.html#c.PyObject_GenericSetAttr)
                Exceptions.SetError(Exceptions.AttributeError, exception.Message);
                return -1;
            }

            return 0;
        }

        private static bool TryGetNonDynamicMember(BorrowedReference ob, BorrowedReference key, out NewReference value, bool clearExceptions = false)
        {
            value = Runtime.PyObject_GenericGetAttr(ob, key);
            // If AttributeError was raised, we try to get the attribute from the managed object dynamic properties.
            var result = !Exceptions.ExceptionMatches(Exceptions.AttributeError);

            if (clearExceptions)
            {
                Exceptions.Clear();
            }

            return result;
        }
    }
}
