using System.Diagnostics;
using fnP2000.Media;

// ReSharper disable InconsistentNaming

namespace fnP2000.Devices
{
    public class MdcrDevice : IDevice
    {
        private MDCR_Status status;
        private MDCR_Control control;

        private const ulong period = 209;

        private MiniTape tape;
        private ulong  tickCount;
        private bool phaseOld;
        private bool phaseLocked;
        private int phaseCount;

        public MiniTape Tape => tape;
        public void Reset()
        {   
            status = MDCR_Status.BET;

            if (tape == null) 
            {
                status |= MDCR_Status.CIP | MDCR_Status.WEN;
            }
            else
            {
                if (!tape.Protected)
                {
                    status |= MDCR_Status.WEN;
                }
            }
            control = MDCR_Control.All;
        }

        public byte Read(byte port)
        {
            // only bits 7,6,5,4,3 are MDCR related
            return (byte) status;
        }

        public void Write(byte port, byte value)
        {
            // only lower 4 bits are for the MDCR
            var newControl = (MDCR_Control)(value & (byte)MDCR_Control.All);
            if (newControl == control) return;
#if DEBUG
            if ((newControl&~MDCR_Control.WDA) != (control&~MDCR_Control.WDA))
                Debug.WriteLine($"{value:X2}");
#endif
            // when motor state changes, BET flag is always reset
            if ((control & MDCR_Control.Run) != (newControl & MDCR_Control.Run))
            {
                status |= MDCR_Status.BET;
#if DEBUG
                Debug.WriteLine($"BET reset, status = {(byte)status:X2} tapePos = {tape?.Position}");
#endif
            }
            control = newControl;
        }

        public void InsertTape()
        {
            // CIP and default is WEN, pos unknown, assume NOT EOT
            status = MDCR_Status.BET;
            tape = new MiniTape();
        }

        public void EjectTape()
        {
            tape = null;
            Reset();
        }

        public void Tick(ulong ticks = 1)
        {
            tickCount += ticks;
            while(tickCount >= period)
            {
                tickCount -= period;
                // when tape is not running, there is nothing to do
                if (!control.HasFlag(MDCR_Control.REV) && !control.HasFlag(MDCR_Control.FWD)) continue;


                // first move the tape
                if (control.HasFlag(MDCR_Control.REV))
                    tape.Reverse();
                if (control.HasFlag(MDCR_Control.FWD))
                    tape.Forward();

                // now handle the read/write head
                if (control.HasFlag(MDCR_Control.WCD))
                {
                    // write phase to tape
                    tape.Write(control.HasFlag(MDCR_Control.WDA));
                }
                else
                {
                    var phaseNew = tape.Read();
                    if (phaseLocked)
                    {
                        if (++phaseCount == 2)
                        {
                            // when locked, every 2nd Phase must be different from the first
                            if (phaseOld != phaseNew)
                            {
                                BitToStatus(phaseNew, control.HasFlag(MDCR_Control.REV));
                                phaseCount = 0;
                            }
                            else
                            {
                                // 2nd phase is not different: no hi-lo or lo-hi
                                // transition detected. That means we lost our lock
                                // and must resynchronize
                                phaseLocked = false;
                            }
                        }
                    }
                    else
                    {
                        // phase change detected?
                        if (phaseOld != phaseNew)
                        {
                            // yes! this may flag beginning of real data 
                            // first transition is also the first bit
                            BitToStatus(phaseNew, control.HasFlag(MDCR_Control.REV));

                            // we synchronized and from now on expect the data
                            // flow will continue correctly
                            phaseLocked = true;
                            // initialize phase counter, we need it to confirm that
                            // the phase read 2nd count differs from the one read at 1st count
                            phaseCount = 0;
                        }
                    }
                    // always save previous phase
                    phaseOld = phaseNew;
                }
                if (tape.Eot)
                    status &= ~MDCR_Status.BET;
                else
                    status |= MDCR_Status.BET;
            }
        }

        private void BitToStatus(bool newPhase, bool flipBits)
        {
            if (flipBits)
            {
                // todo: is this true? can we find in the 
                // todo: hw description that when the motor is running in
                // todo: reverse the clock-bit is placed on the Data-pin? 

                // for now assume this is the case and
                // flip data bit instead of clock bit
                status ^= MDCR_Status.RDA;

                // this means that the data bit is not relevant
                // we therefore leave it alone.
            }
            else
            {
                // flip clock bit
                status ^= MDCR_Status.RDC;
                // and set data bit as well
                if (!newPhase)
                    status |= MDCR_Status.RDA;
                else
                    status &= ~MDCR_Status.RDA;
            }
        }
    }
}
