using System;
using Sandbox.Game.EntityComponents;
using VRage.Game.ModAPI;

namespace TSUT.U235
{
    public static class Storage
    {
        public static void SetFloat(IMyCubeBlock block, Guid key, float value)
        {
            if (block.Storage == null)
            {
                block.Storage = new MyModStorageComponent();
            }

            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return;
            }

            block.Storage[key] = value.ToString();
        }

        public static float GetFloat(IMyCubeBlock block, Guid key, float @default = 0f)
        {
            if (block.Storage == null)
            {
                SetFloat(block, key, @default);
            }
            string valueStr;
            if (block.Storage.TryGetValue(key, out valueStr))
            {
                float value;
                if (float.TryParse(valueStr, out value) && !float.IsNaN(value) && !float.IsInfinity(value))
                    return value;
            }

            SetFloat(block, key, @default);
            return @default;
        }

        public static void SetBool(IMyCubeBlock block, Guid key, bool value)
        {
            if (block.Storage == null)
            {
                block.Storage = new MyModStorageComponent();
            }

            block.Storage[key] = value.ToString();
        }

        public static bool GetBool(IMyCubeBlock block, Guid key, bool @default = false)
        {
            if (block.Storage == null)
            {
                SetBool(block, key, @default);
            }
            string valueStr;
            if (block.Storage.TryGetValue(key, out valueStr))
            {
                bool value;
                if (bool.TryParse(valueStr, out value))
                    return value;
            }

            SetBool(block, key, @default);
            return @default;
        }
    }
}