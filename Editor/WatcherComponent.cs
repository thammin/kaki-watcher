using System;
using System.Linq;
using Kaki.Watcher;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Weaver;
using Weaver.Extensions;

namespace Kaki.Weaver
{
    public static class SomeExtensions
    {
        readonly static string[] WatcherAttributeNames = new string[]
        {
            typeof(ReactiveAttribute).FullName,
            typeof(ComputedAttribute).FullName
        };

        public static bool HasWatcherAttribute(this TypeDefinition self)
        {
            return self.Properties.FirstOrDefault(prop =>
            {
                return prop.CustomAttributes.FirstOrDefault(attr =>
                {
                    return WatcherAttributeNames.Contains(attr.AttributeType.FullName);
                }) != null;
            }) != null;
        }

        public static MethodReference MakeGenericMethod(this MethodReference self, params TypeReference[] arguments)
        {
            if (self.GenericParameters.Count != arguments.Length)
                throw new ArgumentException();

            var instance = new GenericInstanceMethod(self);
            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return instance;
        }

        public static TypeReference MakeGenericType(this TypeReference self, params TypeReference[] arguments)
        {
            if (self.GenericParameters.Count != arguments.Length)
                throw new ArgumentException();

            var instance = new GenericInstanceType(self);
            foreach (var argument in arguments)
                instance.GenericArguments.Add(argument);

            return instance;
        }

        public static MethodReference MakeGeneric(this MethodReference self, params TypeReference[] arguments)
        {
            var reference = new MethodReference(self.Name, self.ReturnType)
            {
                Name = self.Name,
                DeclaringType = self.DeclaringType.MakeGenericType(arguments),
                HasThis = self.HasThis,
                ExplicitThis = self.ExplicitThis,
                ReturnType = self.ReturnType,
                CallingConvention = self.CallingConvention,
            };

            foreach (var parameter in self.Parameters)
                reference.Parameters.Add(new ParameterDefinition(parameter.ParameterType));

            foreach (var generic_parameter in self.GenericParameters)
                reference.GenericParameters.Add(new GenericParameter(generic_parameter.Name, reference));

            return reference;
        }

        public static FieldReference MakeGeneric(this FieldReference self, params TypeReference[] arguments)
        {
            return new FieldReference(
                self.Name,
                self.DeclaringType.MakeGenericType(arguments),
                self.FieldType
            );
        }
    }

    public class WatcherComponent : WeaverComponent
    {
        TypeDefinition _typeDefinition;
        public override string ComponentName => "Watcher";
        public override DefinitionType EffectedDefintions => DefinitionType.Module | DefinitionType.Property;

        public override void VisitType(TypeDefinition typeDefinition)
        {
            if (typeDefinition.HasWatcherAttribute())
            {
                _typeDefinition = typeDefinition;
            }
        }

        public override void VisitProperty(PropertyDefinition propertyDefinition)
        {
            var isReactive = propertyDefinition.GetCustomAttribute<ReactiveAttribute>() != null;
            var isComputed = propertyDefinition.GetCustomAttribute<ComputedAttribute>() != null;

            if (!isReactive && !isComputed) return;

            var module = propertyDefinition.Module;
            var setter = propertyDefinition.SetMethod;
            var getter = propertyDefinition.GetMethod;
            var propertyType = propertyDefinition.PropertyType;
            var watcherType = Import(typeof(Watcher<>)).MakeGenericInstanceType(propertyType);
            var watcherOptionType = Import(typeof(WatcherOption));
            var getterFuncType = Import(typeof(Func<>)).MakeGenericInstanceType(propertyType);

            // watcher field
            var field = new FieldDefinition($"{propertyDefinition.Name}__watcher", FieldAttributes.Private, watcherType);
            _typeDefinition.Fields.Add(field);

            // Func<> ctor
            var getterFuncCtor = module
                .ImportReference(getterFuncType.Resolve().GetMethod(".ctor"))
                .MakeGeneric(propertyType);

            // watcher ctor
            var watcherCtor = module
                .ImportReference(watcherType.Resolve().GetMethod(".ctor"))
                .MakeGeneric(propertyType);

            // watcher option
            var watcherOption = module.ImportReference(watcherOptionType);
            // lazy field
            var watcherLazyField = module.ImportReference(
                watcherOptionType
                    .Resolve()
                    .Fields
                    .First(f => f.Name == "lazy")
            );

            // clone getter method
            var getterMethod = new MethodDefinition($"{getter.Name}__backingMethod", MethodAttributes.Private, propertyType);
            getterMethod.Body.MaxStackSize = getter.Body.MaxStackSize;
            getterMethod.Body.InitLocals = getter.Body.InitLocals;
            getterMethod.Body.LocalVarToken = getter.Body.LocalVarToken;
            _typeDefinition.Methods.Add(getterMethod);

            //
            // getter IL
            //
            {
                var backingProc = getterMethod.Body.GetILProcessor();
                var proc = getter.Body.GetILProcessor();

                // move all instructions
                foreach (var i in getter.Body.Instructions.ToArray())
                {
                    backingProc.Append(i);
                    proc.Remove(i);
                }
                // move all variables
                foreach (var v in getter.Body.Variables.ToArray())
                {
                    getterMethod.Body.Variables.Add(v);
                    getter.Body.Variables.Remove(v);
                }

                // watcher variable
                var watcherVariable = new VariableDefinition(watcherOption);
                getter.Body.Variables.Add(watcherVariable);

                // watcher Get
                var watcherGet = module
                    .ImportReference(watcherType.Resolve().GetMethod("Get"))
                    .MakeGeneric(propertyType);

                var jumpTo = proc.Create(OpCodes.Nop);

                // if (watcher == null)
                proc.Append(proc.Create(OpCodes.Nop));
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldfld, field));
                proc.Append(proc.Create(OpCodes.Ldnull));
                proc.Append(proc.Create(OpCodes.Ceq));
                proc.Append(proc.Create(OpCodes.Brfalse_S, jumpTo));

                // watcher = new Watcher<T>(getter, null, new WatcherOption() { lazy = bool })
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldftn, getterMethod));
                proc.Append(proc.Create(OpCodes.Newobj, getterFuncCtor));
                proc.Append(proc.Create(OpCodes.Ldnull));
                //
                proc.Append(proc.Create(OpCodes.Ldloca_S, watcherVariable));
                proc.Append(proc.Create(OpCodes.Initobj, watcherOption));
                proc.Append(proc.Create(OpCodes.Ldloca_S, watcherVariable));
                proc.Append(proc.Create(isReactive ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1));
                proc.Append(proc.Create(OpCodes.Stfld, watcherLazyField));
                proc.Append(proc.Create(OpCodes.Ldloc_S, watcherVariable));
                //
                proc.Append(proc.Create(OpCodes.Newobj, watcherCtor));
                proc.Append(proc.Create(OpCodes.Stfld, field));

                // return watcher.Get();
                proc.Append(jumpTo);
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldfld, field));
                proc.Append(proc.Create(OpCodes.Callvirt, watcherGet));
                proc.Append(proc.Create(OpCodes.Ret));
            }

            if (!isReactive || setter == null) return;

            // clone setter method
            var setterMethod = new MethodDefinition($"{setter.Name}__backingMethod", MethodAttributes.Private, typeSystem.Void);
            setterMethod.Body.MaxStackSize = setter.Body.MaxStackSize;
            setterMethod.Body.InitLocals = setter.Body.InitLocals;
            setterMethod.Body.LocalVarToken = setter.Body.LocalVarToken;
            _typeDefinition.Methods.Add(setterMethod);

            //
            // setter
            //
            {
                var backingProc = setterMethod.Body.GetILProcessor();
                var proc = setter.Body.GetILProcessor();

                // move all instructions
                foreach (var i in setter.Body.Instructions.ToArray())
                {
                    backingProc.Append(i);
                    proc.Remove(i);
                }
                // copy all variables
                foreach (var v in setter.Body.Variables.ToArray())
                {
                    setterMethod.Body.Variables.Add(v);
                }
                // copy all parameters
                foreach (var p in setter.Parameters.ToArray())
                {
                    setterMethod.Parameters.Add(p);
                }

                // watcher variable
                var watcherVariable = new VariableDefinition(watcherOption);
                setter.Body.Variables.Add(watcherVariable);

                // watcher NotifyDeps
                var watcherNotify = module
                    .ImportReference(watcherType.Resolve().GetMethod("NotifyDeps"))
                    .MakeGeneric(propertyType);

                var jumpTo1 = proc.Create(OpCodes.Nop);
                var jumpTo2 = proc.Create(OpCodes.Nop);

                // if (watcher == null)
                proc.Append(proc.Create(OpCodes.Nop));
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldfld, field));
                proc.Append(proc.Create(OpCodes.Ldnull));
                proc.Append(proc.Create(OpCodes.Ceq));
                proc.Append(proc.Create(OpCodes.Brfalse_S, jumpTo1));

                // watcher = new Watcher<T>(getter, null, new WatcherOption() { lazy = bool })
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldftn, getterMethod));
                proc.Append(proc.Create(OpCodes.Newobj, getterFuncCtor));
                proc.Append(proc.Create(OpCodes.Ldnull));
                //
                proc.Append(proc.Create(OpCodes.Ldloca_S, watcherVariable));
                proc.Append(proc.Create(OpCodes.Initobj, watcherOption));
                proc.Append(proc.Create(OpCodes.Ldloca_S, watcherVariable));
                proc.Append(proc.Create(isReactive ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1));
                proc.Append(proc.Create(OpCodes.Stfld, watcherLazyField));
                proc.Append(proc.Create(OpCodes.Ldloc_S, watcherVariable));
                //
                proc.Append(proc.Create(OpCodes.Newobj, watcherCtor));
                proc.Append(proc.Create(OpCodes.Stfld, field));

                // if (getter() != value)
                proc.Append(jumpTo1);
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Callvirt, getterMethod));
                proc.Append(proc.Create(OpCodes.Ldarg_1));
                proc.Append(proc.Create(OpCodes.Ceq));
                proc.Append(proc.Create(OpCodes.Brtrue_S, jumpTo2));

                // setter(value);
                // watcher.NotifyDeps();
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldarg_1));
                proc.Append(proc.Create(OpCodes.Callvirt, setterMethod));
                proc.Append(proc.Create(OpCodes.Ldarg_0));
                proc.Append(proc.Create(OpCodes.Ldfld, field));
                proc.Append(proc.Create(OpCodes.Callvirt, watcherNotify));

                // return;
                proc.Append(jumpTo2);
                proc.Append(proc.Create(OpCodes.Ret));
            }
        }
    }
}
