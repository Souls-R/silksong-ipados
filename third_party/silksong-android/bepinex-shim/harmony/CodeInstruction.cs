// The transpiler vocabulary.
//
// Nothing here runs. A transpiler rewrites IL while the game is starting, and
// there is no IL left to rewrite once il2cpp has turned the game into C++ --
// mod-weaver reports every plugin that declares one, by name, so that it is
// clear which of a mod's patches were lost.
//
// The types exist anyway, and they have to: a plugin that declares one
// transpiler among twenty ordinary patches still references CodeInstruction
// from its own metadata, and an assembly with an unresolvable type reference
// cannot be converted at all. Carrying the vocabulary is what lets the other
// nineteen patches work.

using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace HarmonyLib
{
    public enum ExceptionBlockType
    {
        BeginExceptionBlock,
        BeginCatchBlock,
        BeginExceptFilterBlock,
        BeginFaultBlock,
        BeginFinallyBlock,
        EndExceptionBlock,
    }

    public class ExceptionBlock
    {
        public ExceptionBlockType blockType;
        public Type catchType;

        public ExceptionBlock(ExceptionBlockType blockType, Type catchType = null)
        {
            this.blockType = blockType;
            this.catchType = catchType;
        }
    }

    public class CodeInstruction
    {
        public OpCode opcode;
        public object operand;
        public List<Label> labels = new List<Label>();
        public List<ExceptionBlock> blocks = new List<ExceptionBlock>();

        public CodeInstruction(OpCode opcode, object operand = null)
        {
            this.opcode = opcode;
            this.operand = operand;
        }

        public CodeInstruction(CodeInstruction instruction)
        {
            opcode = instruction.opcode;
            operand = instruction.operand;
            labels = new List<Label>(instruction.labels);
            blocks = new List<ExceptionBlock>(instruction.blocks);
        }

        public CodeInstruction Clone() { return new CodeInstruction(this); }

        public CodeInstruction Clone(OpCode opcode)
        {
            var clone = Clone();
            clone.opcode = opcode;
            return clone;
        }

        public CodeInstruction Clone(object operand)
        {
            var clone = Clone();
            clone.operand = operand;
            return clone;
        }

        public static CodeInstruction Call(Type type, string name, Type[] parameters = null, Type[] generics = null)
        {
            return new CodeInstruction(OpCodes.Call, AccessTools.Method(type, name, parameters, generics));
        }

        public static CodeInstruction LoadField(Type type, string name, bool useAddress = false)
        {
            return new CodeInstruction(useAddress ? OpCodes.Ldflda : OpCodes.Ldfld, AccessTools.Field(type, name));
        }

        public static CodeInstruction StoreField(Type type, string name)
        {
            return new CodeInstruction(OpCodes.Stfld, AccessTools.Field(type, name));
        }

        public override string ToString()
        {
            return operand == null ? opcode.ToString() : opcode + " " + operand;
        }
    }

    public static class Transpilers
    {
        public static IEnumerable<CodeInstruction> Manipulator(
            IEnumerable<CodeInstruction> instructions,
            Func<CodeInstruction, bool> predicate,
            Action<CodeInstruction> action)
        {
            return instructions;
        }
    }
}
