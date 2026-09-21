using RootSystem = System;
using System.Linq;
using System.Collections.Generic;

namespace Windows.Kinect
{
    // Windows.Kinect.KinectForUnityUtils
    public sealed partial class KinectForUnityUtils
    {
        [RootSystem.Runtime.InteropServices.DllImport("KinectForUnity", CallingConvention=RootSystem.Runtime.InteropServices.CallingConvention.Cdecl, SetLastError=true)]
        private static extern void KinectForUnity_FreeMemory(RootSystem.IntPtr pToDealloc);
        public static void FreeMemory(RootSystem.IntPtr pToDealloc)
        {
            KinectForUnity_FreeMemory(pToDealloc);
        }
    }
}
