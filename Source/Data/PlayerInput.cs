
namespace Celeste64;

/// <summary>
/// Per-player input. In single player this just wraps the normal Controls.
/// In two player mode each player reads their own devices directly:
///  - Player 1: keyboard (+ the first gamepad if two or more gamepads are connected)
///  - Player 2: the second gamepad (or the only gamepad if just one is connected)
/// </summary>
public sealed class PlayerInput
{
	public sealed class Stick(Func<Vec2> read)
	{
		public Vec2 Value => read();
	}

	public sealed class Button(Func<bool> down, Func<bool> pressed, Func<bool> consume)
	{
		public bool Down => down();
		public bool Pressed => pressed();
		public bool ConsumePress() => consume();
	}

	public readonly Stick Move;
	public readonly Stick Camera;
	public readonly Button Jump;
	public readonly Button Dash;
	public readonly Button Climb;

	private readonly List<Action<float>> ticks;

	private PlayerInput(Stick move, Stick camera, Button jump, Button dash, Button climb, List<Action<float>> ticks)
	{
		Move = move;
		Camera = camera;
		Jump = jump;
		Dash = dash;
		Climb = climb;
		this.ticks = ticks;
	}

	/// <summary>Must be called once per update by the owning player</summary>
	public void Tick(float dt)
	{
		foreach (var tick in ticks)
			tick(dt);
	}

	public static PlayerInput FromControls(Controls c)
	{
		static Button Wrap(VirtualAction a) => new(() => a.Down, () => a.Pressed, () => a.ConsumePress());
		return new PlayerInput(
			new Stick(() => c.Move.Value),
			new Stick(() => c.Camera.Value),
			Wrap(c.Jump), Wrap(c.Dash), Wrap(c.Climb),
			[]);
	}

	private sealed class Devices(Input input, int slot)
	{
		public int Pad = -1;

		private static bool IsConnected(Input input, int index)
		{
			try { return input.Controllers[index].Connected; }
			catch { return false; }
		}

		public void Refresh()
		{
			var connected = new List<int>();
			for (int i = 0; i < 8; i++)
				if (IsConnected(input, i))
					connected.Add(i);

			if (slot == 0)
				Pad = connected.Count >= 2 ? connected[0] : -1;
			else
				Pad = connected.Count >= 2 ? connected[1] : (connected.Count == 1 ? connected[0] : -1);
		}

		public bool Key(params Keys[] keys)
		{
			if (slot != 0)
				return false;
			foreach (var k in keys)
				if (input.Keyboard.Down(k))
					return true;
			return false;
		}

		public bool PadDown(params Buttons[] buttons)
		{
			if (Pad < 0)
				return false;
			foreach (var b in buttons)
				if (input.Controllers[Pad].Down(b))
					return true;
			return false;
		}

		public float PadAxis(Axes axis)
			=> Pad >= 0 ? input.Controllers[Pad].Axis(axis) : 0;

		private Vec2 PadStick(Axes x, Axes y, float deadzone)
		{
			var v = new Vec2(PadAxis(x), PadAxis(y));
			return v.Length() < deadzone ? Vec2.Zero : v;
		}

		public Vec2 Move()
		{
			if (slot == 0)
			{
				var k = new Vec2(
					(Key(Keys.Right) ? 1 : 0) - (Key(Keys.Left) ? 1 : 0),
					(Key(Keys.Down) ? 1 : 0) - (Key(Keys.Up) ? 1 : 0));
				if (k != Vec2.Zero)
					return k;
			}
			return PadStick(Axes.LeftX, Axes.LeftY, 0.35f);
		}

		public Vec2 Camera()
		{
			if (slot == 0)
			{
				var k = new Vec2(
					(Key(Keys.D) ? 1 : 0) - (Key(Keys.A) ? 1 : 0),
					(Key(Keys.S) ? 1 : 0) - (Key(Keys.W) ? 1 : 0));
				if (k != Vec2.Zero)
					return k;
			}
			return PadStick(Axes.RightX, Axes.RightY, 0.35f);
		}

		public bool Jump() => Key(Keys.C, Keys.Space) || PadDown(Buttons.South, Buttons.North);
		public bool Dash() => Key(Keys.X) || PadDown(Buttons.West, Buttons.East);
		public bool Climb() 
			=> Key(Keys.Z, Keys.V, Keys.LeftShift, Keys.RightShift, Keys.LeftControl, Keys.RightControl)
			|| PadDown(Buttons.LeftShoulder, Buttons.RightShoulder)
			|| PadAxis(Axes.LeftTrigger) > 0.4f || PadAxis(Axes.RightTrigger) > 0.4f;
	}

	public static PlayerInput FromDevices(Input input, int slot)
	{
		var dev = new Devices(input, slot);
		var ticks = new List<Action<float>> { _ => dev.Refresh() };

		Button Raw(Func<bool> down, float buffer)
		{
			bool was = false;
			float t = 0;
			ticks.Add(dt =>
			{
				bool d = down();
				if (d && !was)
					t = buffer;
				else if (t > 0)
					t -= dt;
				was = d;
			});
			return new Button(down, () => t > 0, () =>
			{
				if (t > 0)
				{
					t = 0;
					return true;
				}
				return false;
			});
		}

		var jump = Raw(dev.Jump, .1f);
		var dash = Raw(dev.Dash, .1f);
		var climb = Raw(dev.Climb, 0f);
		return new PlayerInput(new Stick(dev.Move), new Stick(dev.Camera), jump, dash, climb, ticks);
	}

	public static PlayerInput Create(Game game, bool twoPlayer, int playerIndex)
		=> twoPlayer
			? FromDevices(game.Input, playerIndex)
			: FromControls(game.Controls);
}
