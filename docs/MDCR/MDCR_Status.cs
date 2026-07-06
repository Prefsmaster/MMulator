using System;

namespace fnP2000.Devices
{
    [Flags]
    public enum MDCR_Status
    {
        WEN = 0x08,        
        CIP = 0x10,        
        BET = 0x20,        
        RDC = 0x40,        
        RDA = 0x80,
        All = WEN + CIP + BET + RDC + RDA
    }
}