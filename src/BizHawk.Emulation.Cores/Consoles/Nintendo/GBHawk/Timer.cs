﻿using System;
using BizHawk.Common;
using BizHawk.Common.NumberExtensions;

namespace BizHawk.Emulation.Cores.Nintendo.GBHawk
{
	// Timer Emulation
	// NOTES: 
	//
	// For GB, it looks like divider reg should start at 0 on reset. however in this case, in order to pass Gambatte timer tests, 
	// reads from timer when timer is about to rollover need to return the next value
	// for GBC, a starting value of 0xFFFF passes all tests. GBA is not explicotlu tested but for now is set to 0xFFFF as well.
	//
	// Some additional glitches happen on GBC, but they are non-deterministic and not emulated here
	//
	// TODO: On GBA models, there is a race condition when enabling with a change in bit check
	// that would result in a state change that is not consistent in all models, see tac_set_disabled.gbc
	//
	// TODO: On GBA only, there is a glitch where if the current timer control is 7 and the written value is 7 and
	// there is a coincident timer increment, there will be an additional increment along with this write.
	// not sure it effects all models or of exact details, see test tac_set_timer_disabled.gbc

	public class Timer
	{
		public GBHawk Core { get; set; }

		public ushort divider_reg;
		public byte timer_reload;
		public byte timer;
		public byte timer_old;
		public byte timer_control;
		public byte pending_reload;
		public bool IRQ_block; // if the timer IRQ happens on the same cycle as a previous one was cleared, the IRQ is blocked
		public bool old_state;
		public bool state;
		public bool reload_block;
		public ulong next_free_cycle;
		
		public byte ReadReg(int addr)
		{
			byte ret = 0;

			switch (addr)
			{
				case 0xFF04: ret = (byte)(divider_reg >> 8);		break; // DIV register
				case 0xFF05: ret = timer;							break; // TIMA (Timer Counter)
				case 0xFF06: ret = timer_reload;					break; // TMA (Timer Modulo)
				case 0xFF07: ret = timer_control;					break; // TAC (Timer Control)
			}

			return ret;
		}

		public void WriteReg(int addr, byte value)
		{
			switch (addr)
			{
				// DIV register
				case 0xFF04:
					divider_reg = 0;
					break;

				// TIMA (Timer Counter)
				case 0xFF05:
					if (Core.cpu.TotalExecutedCycles >= next_free_cycle)
					{
						timer_old = timer;
						timer = value;
						reload_block = true;
					}
					break;

				// TMA (Timer Modulo)
				case 0xFF06:
					timer_reload = value;
					if (Core.cpu.TotalExecutedCycles < next_free_cycle)
					{
						timer = timer_reload;
						timer_old = timer;
					}
					break;

				// TAC (Timer Control)
				case 0xFF07:

					bool was_off = (timer_control & 4) == 0;
					//Console.WriteLine(timer_control + " " + value + " " + timer + " " + divider_reg);
					timer_control = (byte)((timer_control & 0xf8) | (value & 0x7)); // only bottom 3 bits function
					/*
					if (was_off && ((timer_control & 4) > 0) && Core.is_GBC)
					{
						bool temp_check = false;

						switch (timer_control & 3)
						{
							case 0:
								temp_check = (divider_reg & 0x1FF) == 0x1FF;
								break;
							case 1:
								temp_check = (divider_reg & 0x7) == 0x7;
								break;
							case 2:
								temp_check = (divider_reg & 0x1F) == 0x1F;
								break;
							case 3:
								temp_check = (divider_reg & 0x7F) == 0x7F;
								break;
						}

						if (temp_check)
						{
							timer_old = timer;
							timer++;
							Console.WriteLine("glitch");
							// if overflow happens, set the interrupt flag and reload the timer (if applicable)
							if (timer < timer_old)
							{
								if (timer_control.Bit(2))
								{
									pending_reload = 4;
									reload_block = false;
								}
								else
								{
									//TODO: Check if timer still gets reloaded if TAC diabled causes overflow
									if (Core.REG_FFFF.Bit(2)) { Core.cpu.FlagI = true; }
									Core.REG_FF0F |= 0x04;
								}
							}
						}
					}
					*/
					break;
			}
		}

		public void tick()
		{
			divider_reg++;

			// pick a bit to test based on the current value of timer control
			switch (timer_control & 3)
			{
				case 0:
					state = divider_reg.Bit(9);
					break;
				case 1:
					state = divider_reg.Bit(3);
					break;
				case 2:
					state = divider_reg.Bit(5);
					break;
				case 3:
					state = divider_reg.Bit(7);
					break;
			}

			// And it with the state of the timer on/off bit
			state &= timer_control.Bit(2);

			// this procedure allows several glitchy timer ticks, since it only measures falling edge of the state
			// so things like turning the timer off and resetting the divider will tick the timer
			if (old_state && !state)
			{
				timer_old = timer;
				timer++;

				// if overflow happens, set the interrupt flag and reload the timer (if applicable)
				if (timer < timer_old)
				{
					if (timer_control.Bit(2))
					{
						pending_reload = 4;
						reload_block = false;
					}
					else
					{
						pending_reload = 3;
						reload_block = false;
					}					
				}
			}

			old_state = state;

			if (pending_reload > 0)
			{
				pending_reload--;
				if (pending_reload == 0 && !reload_block)
				{
					timer = timer_reload;
					timer_old = timer;

					next_free_cycle = 4 + Core.cpu.TotalExecutedCycles;

					// set interrupts
					if (!IRQ_block)
					{
						if (Core.REG_FFFF.Bit(2)) { Core.cpu.FlagI = true; }
						Core.REG_FF0F |= 0x04;
					}
				}
			}

			IRQ_block = false;
		}

		public void Reset()
		{
			divider_reg = (ushort)(Core.is_GBC ? 0xFFFF : 0);
			timer_reload = 0;
			timer = 0;
			timer_old = 0;
			timer_control = 0xF8;
			pending_reload = 0;
			IRQ_block = false;
			old_state = false;
			state = false;
			reload_block = false;
			next_free_cycle = 0;
		}

		public void SyncState(Serializer ser)
		{
			ser.Sync(nameof(divider_reg), ref divider_reg);
			ser.Sync(nameof(timer_reload), ref timer_reload);
			ser.Sync(nameof(timer), ref timer);
			ser.Sync(nameof(timer_old), ref timer_old);
			ser.Sync(nameof(timer_control), ref timer_control);
			ser.Sync(nameof(pending_reload), ref pending_reload);
			ser.Sync(nameof(IRQ_block), ref IRQ_block);
			ser.Sync(nameof(old_state), ref old_state);
			ser.Sync(nameof(state), ref state);
			ser.Sync(nameof(reload_block), ref reload_block);
			ser.Sync(nameof(next_free_cycle), ref next_free_cycle);
		}
	}
}