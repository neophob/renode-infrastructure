//
// Copyright (c) 2010-2020 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Antmicro.Renode.Logging;

namespace Antmicro.Renode.Peripherals.Timers
{
    // This class does not implement advanced-control timers interrupts
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord)]
    public class STM32_Timer : LimitTimer, IDoubleWordPeripheral
    {
        public STM32_Timer(Machine machine, long frequency, uint initialLimit) : base(machine.ClockSource, frequency, limit: initialLimit,  direction: Direction.Ascending, enabled: false, autoUpdate: false)
        {
            IRQ = new GPIO();
            this.initialLimit = initialLimit;

            LimitReached += delegate
            {
                if(updateDisable.Value)
                {
                    return;
                }
                if(updateInterruptEnable.Value)
                {
                    this.Log(LogLevel.Noisy, "IRQ pending");
                    updateInterruptFlag = true;
                }
                Limit = autoReloadValue;

                for(var i = 0; i < NumberOfCCChannels; ++i)
                {
                    ccTimers[i].Enabled = Enabled && ccTimers[i].EventEnabled;
                }
                UpdateInterrupts();
            };

            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                var j = i;
                ccTimers[j] = new LimitTimer(machine.ClockSource, frequency, this, String.Format("cctimer{0}", j + 1), limit: initialLimit, eventEnabled: true, direction: Direction.Ascending, enabled: false, autoUpdate: false);
                ccTimers[j].LimitReached += delegate
                {
                    ccTimers[j].Enabled = false;
                    ccInterruptFlag[j] = true;
                    this.Log(LogLevel.Noisy, "cctimer{0}: Compare IRQ pending", j + 1);
                    UpdateInterrupts();
                };
            }

            var registersMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.Control1, new DoubleWordRegister(this)
                    .WithFlag(0, writeCallback: (_, val) => Enabled = val, valueProviderCallback: _ => Enabled, name: "Counter enable (CEN)")
                    .WithFlag(1, out updateDisable, name: "Update disable (UDIS)")
                    .WithFlag(2, out updateRequestSource, name: "Update request source (URS)")
                    .WithFlag(3, writeCallback: (_, val) => Mode = val ? WorkMode.OneShot : WorkMode.Periodic, valueProviderCallback: _ => Mode == WorkMode.OneShot, name: "One-pulse mode (OPM)")
                    .WithFlag(4, writeCallback: (_, val) => Direction = val ? Direction.Descending : Direction.Ascending, valueProviderCallback: _ => Direction == Direction.Descending, name: "Direction (DIR)")
                    .WithTag("Center-aligned mode selection (CMS)", 5, 2)
                    .WithFlag(7, out autoReloadPreloadEnable, name: "Auto-reload preload enable (APRE)")
                    .WithTag("Clock Division (CKD)", 8, 2)
                    .WithReservedBits(10, 22)
                    .WithWriteCallback((_, __) => { UpdateCaptureCompareTimers(); UpdateInterrupts(); })
                },

                {(long)Registers.DmaOrInterruptEnable, new DoubleWordRegister(this)
                    .WithFlag(0, out updateInterruptEnable, name: "Update interrupt enable (UIE)")
                    .WithFlag(1, valueProviderCallback: _ => ccTimers[0].EventEnabled, writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(0, val), name: "Capture/Compare 1 interrupt enable (CC1IE)")
                    .WithFlag(2, valueProviderCallback: _ => ccTimers[1].EventEnabled, writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(1, val), name: "Capture/Compare 2 interrupt enable (CC2IE)")
                    .WithFlag(3, valueProviderCallback: _ => ccTimers[2].EventEnabled, writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(2, val), name: "Capture/Compare 3 interrupt enable (CC3IE)")
                    .WithFlag(4, valueProviderCallback: _ => ccTimers[3].EventEnabled, writeCallback: (_, val) => WriteCaptureCompareInterruptEnable(3, val), name: "Capture/Compare 4 interrupt enable (CC4IE)")
                    .WithReservedBits(5, 1)
                    .WithTag("Trigger interrupt enable (TIE)", 6, 1)
                    .WithReservedBits(7, 1)
                    .WithTag("Update DMA request enable (UDE)", 8, 1)
                    .WithTag("Capture/Compare 1 DMA request enable (CC1DE)", 9, 1)
                    .WithTag("Capture/Compare 2 DMA request enable (CC2DE)", 10, 1)
                    .WithTag("Capture/Compare 3 DMA request enable (CC3DE)", 11, 1)
                    .WithTag("Capture/Compare 4 DMA request enable (CC4DE)", 12, 1)
                    .WithReservedBits(13, 1)
                    .WithTag("Trigger DMA request enable (TDE)", 14, 1)
                    .WithReservedBits(15, 17)
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.Status, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read | FieldMode.WriteZeroToClear,
                        writeCallback: (_, val) =>
                        {
                            if(!val)
                            {
                                updateInterruptFlag = false;
                                this.Log(LogLevel.Noisy, "IRQ claimed");
                            }
                        },
                        valueProviderCallback: (_) =>
                        {
                            return updateInterruptFlag;
                        },
                        name: "Update interrupt flag (UIF)")
                    .WithFlag(1, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(0, val), valueProviderCallback: _ => ccInterruptFlag[0], name: "Capture/Compare 1 interrupt flag (CC1IF)")
                    .WithFlag(2, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(1, val), valueProviderCallback: _ => ccInterruptFlag[1], name: "Capture/Compare 2 interrupt flag (CC2IF)")
                    .WithFlag(3, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(2, val), valueProviderCallback: _ => ccInterruptFlag[2], name: "Capture/Compare 3 interrupt flag (CC3IF)")
                    .WithFlag(4, FieldMode.Read | FieldMode.WriteZeroToClear, writeCallback: (_, val) => ClaimCaptureCompareInterrupt(3, val), valueProviderCallback: _ => ccInterruptFlag[3], name: "Capture/Compare 4 interrupt flag (CC4IF)")
                    .WithReservedBits(5, 1)
                    // These write callbacks are here only to prevent from very frequent logging.
                    .WithValueField(6, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Trigger interrupt flag (TIE)")
                    .WithReservedBits(7, 2)
                    .WithValueField(9, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 1 overcapture flag (CC1OF)")
                    .WithValueField(10, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 2 overcapture flag (CC2OF)")
                    .WithValueField(11, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 3 overcapture flag (CC3OF)")
                    .WithValueField(12, 1, FieldMode.WriteZeroToClear, writeCallback: (_, __) => {}, name: "Capture/Compare 4 overcapture flag (CC4OF)")
                    .WithReservedBits(13, 18)
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.EventGeneration, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.WriteOneToClear, writeCallback: (_, val) =>
                    {
                        if(updateDisable.Value)
                        {
                            return;
                        }
                        if(Direction == Direction.Ascending)
                        {
                            Value = 0;
                        }
                        else if(Direction == Direction.Descending)
                        {
                            Value = autoReloadValue;
                        }
                        if(!updateRequestSource.Value && updateInterruptEnable.Value)
                        {
                            this.Log(LogLevel.Noisy, "IRQ pending");
                            updateInterruptFlag = true;
                        }
                        for(var i = 0; i < NumberOfCCChannels; ++i)
                        {
                            if(ccTimers[i].Enabled)
                            {
                                ccTimers[i].Value = Value;
                            }
                        }
                    }, name: "Update generation (UG)")
                    .WithTag("Capture/compare 1 generation (CC1G)", 1, 1)
                    .WithTag("Capture/compare 2 generation (CC2G)", 2, 1)
                    .WithTag("Capture/compare 3 generation (CC3G)", 3, 1)
                    .WithTag("Capture/compare 4 generation (CC4G)", 4, 1)
                    .WithReservedBits(5, 1)
                    .WithTag("Trigger generation (TG)", 6, 1)
                    .WithReservedBits(7, 25)
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },

                {(long)Registers.Counter, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, val) => Value = val, valueProviderCallback: _ => (uint)Value, name: "Counter value (CNT)")
                    .WithWriteCallback((_, val) =>
                    {
                        for(var i = 0; i < NumberOfCCChannels; ++i)
                        {
                            if(val < ccTimers[i].Limit)
                            {
                                ccTimers[i].Value = val;
                            }
                        }
                        UpdateInterrupts();
                    })
                },

                {(long)Registers.Prescaler, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, val) => Divider = (int)val + 1, valueProviderCallback: _ => (uint)Divider - 1, name: "Prescaler value (PSC)")
                    .WithWriteCallback((_, __) =>
                    {
                        for(var i = 0; i < NumberOfCCChannels; ++i)
                        {
                            ccTimers[i].Divider = Divider;
                        }
                        UpdateInterrupts();
                    })
                },

                {(long)Registers.AutoReload, new DoubleWordRegister(this)
                    .WithValueField(0, 32, writeCallback: (_, val) =>
                    {
                        autoReloadValue = val;
                        if(!autoReloadPreloadEnable.Value)
                        {
                            Limit = autoReloadValue;
                        }
                    }, valueProviderCallback: _ => autoReloadValue, name: "Counter value (CNT)")
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },
            };

            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                var j = i;
                registersMap.Add((long)Registers.CaptureOrCompare1 + (j * 0x4), new DoubleWordRegister(this)
                    .WithValueField(0, 32, valueProviderCallback: _ => (uint)ccTimers[j].Limit, writeCallback: (_, val) => { ccTimers[j].Limit = val; }, name: String.Format("Capture/compare value {0} (CCR{0})", j + 1))
                    .WithWriteCallback((_, __) => { UpdateCaptureCompareTimer(j); UpdateInterrupts(); })
                );
            }

            registers = new DoubleWordRegisterCollection(this, registersMap);
            Reset();

            EventEnabled = true;
        }

        public uint ReadDoubleWord(long offset)
        {
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            registers.Write(offset, value);
        }

        public override void Reset()
        {
            base.Reset();
            registers.Reset();
            autoReloadValue = initialLimit;
            Limit = initialLimit;
            updateInterruptFlag = false;
            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                ccTimers[i].Reset();
                ccInterruptFlag[i] = false;
            }
            UpdateInterrupts();
        }

        public GPIO IRQ { get; private set; }

        public long Size => 0x400;

        private void UpdateCaptureCompareTimer(int i)
        {
            ccTimers[i].Enabled = Enabled && ccTimers[i].EventEnabled && Value < ccTimers[i].Limit;
            if(ccTimers[i].Enabled)
            {
                ccTimers[i].Value = Value;
            }
            ccTimers[i].Mode = Mode;
            ccTimers[i].Direction = Direction;
        }

        private void UpdateCaptureCompareTimers()
        {
            for(var i = 0; i < NumberOfCCChannels; ++i)
            {
                UpdateCaptureCompareTimer(i);
            }
        }

        private void WriteCaptureCompareInterruptEnable(int i, bool value)
        {
            ccTimers[i].EventEnabled = value;
            UpdateCaptureCompareTimer(i);
            this.Log(LogLevel.Noisy, "cctimer{0}: Interrupt Enable set to {1}", i + 1, value);
        }

        private void ClaimCaptureCompareInterrupt(int i, bool value)
        {
            if(!value)
            {
                ccInterruptFlag[i] = false;
                this.Log(LogLevel.Noisy, "cctimer{0}: Compare IRQ claimed", i + 1);
            }
        }

        private void UpdateInterrupts()
        {
            var value = false;
            value |= updateInterruptFlag & updateInterruptEnable.Value;
            for(var i  = 0; i < NumberOfCCChannels; ++i)
            {
                value |= ccInterruptFlag[i] & ccTimers[i].EventEnabled;
            }

            IRQ.Set(value);
        }

        private uint initialLimit;
        private uint autoReloadValue;
        private bool updateInterruptFlag;
        private bool[] ccInterruptFlag = new bool[NumberOfCCChannels];
        private readonly IFlagRegisterField updateDisable;
        private readonly IFlagRegisterField updateRequestSource;
        private readonly IFlagRegisterField updateInterruptEnable;
        private readonly IFlagRegisterField autoReloadPreloadEnable;
        private readonly DoubleWordRegisterCollection registers;
        private readonly LimitTimer[] ccTimers = new LimitTimer[NumberOfCCChannels];

        private const int NumberOfCCChannels = 4;

        private enum Registers : long
        {
            Control1 = 0x0,
            Control2 = 0x04,
            SlaveModeControl = 0x08,
            DmaOrInterruptEnable = 0x0C,
            Status = 0x10,
            EventGeneration = 0x14,
            CaptureOrCompareMode1 = 0x18,
            CaptureOrCompareMode2 = 0x1C,
            CaptureOrCompareEnable = 0x20,
            Counter = 0x24,
            Prescaler = 0x28,
            AutoReload = 0x2C,
            // gap intended
            CaptureOrCompare1 = 0x34,
            CaptureOrCompare2 = 0x38,
            CaptureOrCompare3 = 0x3C,
            CaptureOrCompare4 = 0x40,
            // gap intended
            DmaControl = 0x48,
            DmaAddressForFullTransfer = 0x4C,
            Option = 0x50
        }
    }
}

