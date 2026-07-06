using System;

namespace fnP2000.Devices
{
    [Flags]
    public enum MDCR_Control
    {
        WDA = 0x01,        
        WCD = 0x02,        
        REV = 0x04,        
        FWD = 0x08,
        All = WDA + WCD + REV + FWD,
        Run = REV + FWD
    }
}