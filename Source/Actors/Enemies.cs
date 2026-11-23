
namespace Celeste64;

/// <summary>
/// Anything the players can fight: normal enemies and the two bosses.
/// None of them have faces on purpose.
/// </summary>
public abstract class Hostile : Actor, IHaveModels, ICastPointShadow
{
	public int Health;
	public readonly int MaxHealth;
	public bool IsDead;
	public bool IsBoss;
	public bool Engaged;
	public string Title = string.Empty;
	public float HitRadius = 7;
	public Vec3 CenterOffset = Vec3.Zero;
	public float PointShadowAlpha { get; set; } = 1.0f;

	protected readonly SkinnedModel Model;
	protected Vec3 Knock;
	protected float KnockSpeed = 55;
	protected float ContactRadius = 8;
	protected int ContactDamage = 1;
	protected float ModelScale = 1;
	protected float SpinSpeed = 1;
	protected float BobHeight = 1.5f;
	protected Vec3 Home;
	protected float Age;

	private float tInvuln;
	private float tFlash;

	protected Hostile(string modelName, int health)
	{
		MaxHealth = Health = health;
		Model = new(Assets.Models[modelName]);
		Model.Flags = ModelFlags.Default;
		Model.MakeMaterialsUnique();
	}

	public override void Added()
	{
		Home = Position;
		LocalBounds = new BoundingBox(Vec3.Zero, HitRadius + 6);
		World.HasCombat = true;
		if (World.Get<CombatHud>() == null)
			World.Add(new CombatHud());
	}

	protected abstract void AI();
	protected virtual void OnHit() { }
	protected virtual float Squash() => 1;
	protected virtual Color TintColor() => Color.White;

	public override void Update()
	{
		if (IsDead)
			return;

		Age += Time.Delta;
		if (tInvuln > 0)
			tInvuln -= Time.Delta;
		if (tFlash > 0)
			tFlash -= Time.Delta;

		// knockback
		if (Knock != Vec3.Zero)
		{
			Position += Knock * Time.Delta;
			float len = Knock.Length() - 300 * Time.Delta;
			Knock = len > 0.5f ? Knock.Normalized() * len : Vec3.Zero;
		}

		AI();
		ContactCheck();
	}

	protected Player? NearestPlayer(float maxDistance)
	{
		Player? best = null;
		float bestDist = maxDistance * maxDistance;
		foreach (var actor in World.All<Player>())
		{
			if (actor is Player p && !p.Dead)
			{
				float d = (p.Position + Vec3.UnitZ * 5 - Position).LengthSquared();
				if (d < bestDist)
				{
					bestDist = d;
					best = p;
				}
			}
		}
		return best;
	}

	protected void ReturnHome(float speed)
	{
		var d = Home - Position;
		if (d.LengthSquared() > 4)
			Position += d.Normalized() * speed * Time.Delta;
	}

	protected void KeepHeight(float below, float above)
	{
		Position = new Vec3(Position.X, Position.Y, MathF.Min(MathF.Max(Position.Z, Home.Z - below), Home.Z + above));
	}

	private void ContactCheck()
	{
		foreach (var actor in World.All<Player>())
		{
			if (actor is not Player p || p.Dead)
				continue;

			var delta = p.Position + Vec3.UnitZ * 5 - (Position + CenterOffset);
			float r = ContactRadius + 4;
			if (delta.LengthSquared() > r * r)
				continue;

			if (p.Hurt(Position + CenterOffset, ContactDamage))
			{
				var away = delta.XY();
				away = away.LengthSquared() > 0.001f ? away.Normalized() : Vec2.UnitX;
				Knock = new Vec3(-away * 40, 10);
			}
		}
	}

	/// <summary>Returns true if the hit landed</summary>
	public bool Hit(Actor attacker, Vec3 direction, int damage)
	{
		if (IsDead || tInvuln > 0)
			return false;

		Health -= damage;
		tInvuln = 0.45f;
		tFlash = 0.25f;

		var dir = direction.XY();
		dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vec2.UnitX;
		Knock = new Vec3(dir * KnockSpeed, 22);

		World.HitStun = 0.07f;
		Audio.Play(Sfx.sfx_land, Position);
		Burst(14, new Color(1.0f, 0.9f, 0.6f, 1.0f), 55);
		OnHit();

		if (Health <= 0)
			Die();
		return true;
	}

	protected void Burst(int count, Color color, float speed)
	{
		for (int i = 0; i < count; i++)
		{
			float a = World.Rng.Float() * MathF.Tau;
			float e = World.Rng.Float(-0.5f, 1.0f);
			var dir = new Vec3(MathF.Cos(a), MathF.Sin(a), e).Normalized();
			World.Request<Dust>().Init(
				Position + CenterOffset,
				dir * speed * World.Rng.Float(0.5f, 1.0f),
				color, "particle-star", 4, World.Rng.Float(0.35f, 0.7f), 0);
		}
	}

	private void Die()
	{
		IsDead = true;
		Burst(IsBoss ? 60 : 24, new Color(1.0f, 0.6f, 0.3f, 1.0f), IsBoss ? 90 : 60);
		World.Request<Dust>().Init(Position + CenterOffset, Vec3.Zero, new Color(1.0f, 0.7f, 0.4f, 1.0f) * 0.6f, "circle", IsBoss ? 70 : 30, 0.5f, 0);
		Audio.Play(IsBoss ? Sfx.sfx_secret : Sfx.sfx_glassbreak, Position);
		World.HitStun = IsBoss ? 0.2f : 0.08f;

		// beating a boss heals everybody
		if (IsBoss)
		{
			foreach (var actor in World.All<Player>())
				if (actor is Player p)
					p.Heal(Player.MaxHealth);
		}

		World.Destroy(this);
	}

	public void CollectModels(List<(Actor Actor, Model Model)> populate)
	{
		if (IsDead)
			return;

		Model.Transform =
			Matrix.CreateScale(ModelScale * Squash()) *
			Matrix.CreateRotationZ(Age * SpinSpeed) *
			Matrix.CreateTranslation(Vec3.UnitZ * MathF.Sin(Age * 2.2f) * BobHeight);

		bool flash = tFlash > 0 && ((int)(tFlash * 30) % 2 == 0);
		foreach (var mat in Model.Materials)
			mat.Color = flash ? new Color(1.0f, 0.35f, 0.35f, 1.0f) : TintColor();

		populate.Add((this, Model));
	}
}

/// <summary>Small floating enemy that chases the closest player</summary>
public class Enemy : Hostile
{
	public Enemy() : base("enemy", 3)
	{
		HitRadius = 5.5f;
		ContactRadius = 6;
		SpinSpeed = 2;
		BobHeight = 1.2f;
		KnockSpeed = 60;
	}

	protected override void AI()
	{
		var target = NearestPlayer(75);
		if (target != null)
		{
			var to = target.Position + Vec3.UnitZ * 5 - Position;
			if (to.LengthSquared() > 1)
				Position += to.Normalized() * 24 * Time.Delta;
			KeepHeight(6, 40);
		}
		else
		{
			ReturnHome(10);
		}
	}
}

/// <summary>Boss one: winds up, then charges straight at a player</summary>
public class BossCharger : Hostile
{
	private enum Phase { Idle, Windup, Charge, Recover }

	private Phase phase = Phase.Idle;
	private float tPhase;
	private Vec3 chargeDir = Vec3.UnitX;

	public BossCharger() : base("boss1", 14)
	{
		IsBoss = true;
		Title = "WARDEN";
		HitRadius = 13;
		ContactRadius = 14;
		SpinSpeed = 0.6f;
		BobHeight = 2;
		KnockSpeed = 30;
	}

	protected override float Squash() => phase == Phase.Windup ? 1.0f + MathF.Sin(Age * 45) * 0.07f : 1.0f;
	protected override Color TintColor() => phase == Phase.Windup ? new Color(1.0f, 0.55f, 0.55f, 1.0f) : Color.White;

	protected override void AI()
	{
		var target = NearestPlayer(Engaged ? 420 : 200);
		Engaged = target != null;
		if (target == null)
		{
			phase = Phase.Idle;
			tPhase = 0;
			ReturnHome(14);
			return;
		}

		tPhase += Time.Delta;
		var to = target.Position + Vec3.UnitZ * 5 - Position;
		var dir = to.LengthSquared() > 1 ? to.Normalized() : chargeDir;

		switch (phase)
		{
			case Phase.Idle:
				ContactDamage = 1;
				if ((Position - Home).XY().Length() > 260)
					ReturnHome(30);
				else
					Position += new Vec3(dir.X, dir.Y, 0) * 14 * Time.Delta;
				KeepHeight(10, 30);
				if (tPhase > 2.0f)
				{
					phase = Phase.Windup;
					tPhase = 0;
				}
				break;

			case Phase.Windup:
				chargeDir = dir;
				if (tPhase > 0.9f)
				{
					phase = Phase.Charge;
					tPhase = 0;
					Audio.Play(Sfx.sfx_dash_red, Position);
				}
				break;

			case Phase.Charge:
				ContactDamage = 2;
				Position += chargeDir * 105 * Time.Delta;
				KeepHeight(10, 30);
				if (tPhase > 0.7f || (Position - Home).XY().Length() > 320)
				{
					phase = Phase.Recover;
					tPhase = 0;
				}
				break;

			case Phase.Recover:
				ContactDamage = 1;
				if (tPhase > 1.4f)
				{
					phase = Phase.Idle;
					tPhase = 0;
				}
				break;
		}
	}
}

/// <summary>Boss two: circles around its home and shoots volleys of bolts</summary>
public class BossShooter : Hostile
{
	private float tFire = 1.5f;
	private int volleys;
	private float orbit;

	public BossShooter() : base("boss2", 18)
	{
		IsBoss = true;
		Title = "KEEPER";
		HitRadius = 14;
		ContactRadius = 15;
		SpinSpeed = 0.9f;
		BobHeight = 2.5f;
		KnockSpeed = 25;
	}

	protected override void AI()
	{
		var target = NearestPlayer(Engaged ? 420 : 220);
		Engaged = target != null;
		if (target == null)
		{
			ReturnHome(12);
			return;
		}

		// slowly circle around the home position
		orbit += Time.Delta * 0.45f;
		var goal = Home + new Vec3(MathF.Cos(orbit) * 70, MathF.Sin(orbit) * 70, 0);
		var d = goal - Position;
		float dist = d.Length();
		if (dist > 1)
			Position += d.Normalized() * MathF.Min(40, dist * 2) * Time.Delta;

		tFire -= Time.Delta;
		if (tFire <= 0)
		{
			Fire(target);
			tFire = 2.4f;
		}
	}

	private void Fire(Player target)
	{
		volleys++;
		Audio.Play(Sfx.sfx_dash_pink, Position);

		var aim = target.Position + Vec3.UnitZ * 5 - Position;
		aim = aim.LengthSquared() > 1 ? aim.Normalized() : Vec3.UnitX;

		if (volleys % 3 == 0)
		{
			// ring of bolts
			for (int i = 0; i < 10; i++)
			{
				float a = i / 10.0f * MathF.Tau;
				World.Add(new BossBolt(Position, new Vec3(MathF.Cos(a), MathF.Sin(a), 0) * 42));
			}
		}
		else
		{
			// fan of three bolts at the target
			for (int i = -1; i <= 1; i++)
			{
				float a = i * 0.35f;
				float c = MathF.Cos(a), s = MathF.Sin(a);
				var v = new Vec3(aim.X * c - aim.Y * s, aim.X * s + aim.Y * c, aim.Z);
				World.Add(new BossBolt(Position, v * 52));
			}
		}
	}
}

/// <summary>Projectile fired by the second boss</summary>
public class BossBolt : Actor, IHaveModels
{
	private readonly SkinnedModel model;
	private readonly Vec3 velocity;
	private float life = 5;

	public BossBolt(Vec3 at, Vec3 velocity)
	{
		Position = at;
		this.velocity = velocity;
		UpdateOffScreen = true;
		LocalBounds = new BoundingBox(Vec3.Zero, 4);
		model = new(Assets.Models["bolt"]);
		model.Flags = ModelFlags.Default;
	}

	public override void Update()
	{
		if (Destroying)
			return;

		Position += velocity * Time.Delta;
		life -= Time.Delta;

		if (World.Rng.Float() < 0.5f)
			World.Request<Dust>().Init(Position, Vec3.Zero, new Color(1.0f, 0.6f, 0.3f, 1.0f), "circle", 5, 0.25f, 0);

		foreach (var actor in World.All<Player>())
		{
			if (actor is not Player p || p.Dead)
				continue;

			if ((p.Position + Vec3.UnitZ * 5 - Position).LengthSquared() < 7 * 7 && p.Hurt(Position, 1))
			{
				World.Request<Dust>().Init(Position, Vec3.Zero, new Color(1.0f, 0.6f, 0.3f, 1.0f) * 0.7f, "circle", 14, 0.3f, 0);
				World.Destroy(this);
				return;
			}
		}

		if (life <= 0)
			World.Destroy(this);
	}

	public void CollectModels(List<(Actor Actor, Model Model)> populate)
	{
		model.Transform = Matrix.CreateScale(1.0f) * Matrix.CreateRotationZ(World.GeneralTimer * 6);
		populate.Add((this, model));
	}
}

/// <summary>Hearts, pulse reload bar and boss health bars</summary>
public class CombatHud : Actor, IHaveUI
{
	private static readonly string[] HeartShape =
	[
		".XX.XX.",
		"XXXXXXX",
		"XXXXXXX",
		".XXXXX.",
		"..XXX..",
		"...X..."
	];

	public CombatHud()
	{
		UpdateOffScreen = true;
	}

	private static void DrawHeart(Batcher batch, float x, float y, float s, Color color)
	{
		for (int row = 0; row < HeartShape.Length; row++)
			for (int col = 0; col < HeartShape[row].Length; col++)
				if (HeartShape[row][col] == 'X')
					batch.Rect(new Rect(x + col * s, y + row * s, s, s), color);
	}

	public void RenderUI(Batcher batch, Rect bounds)
	{
		const float s = 2;
		var full = new Color(0.93f, 0.2f, 0.28f, 1.0f);
		var empty = new Color(0.25f, 0.1f, 0.15f, 1.0f) * 0.8f;

		foreach (var actor in World.All<Player>())
		{
			if (actor is not Player p)
				continue;

			bool second = p.PlayerIndex == 1 && World.TwoPlayer;
			float x0 = second ? bounds.Width / 2 + 8 : 8;
			float y0 = World.TwoPlayer ? 30 : 10;

			for (int i = 0; i < Player.MaxHealth; i++)
				DrawHeart(batch, x0 + i * 17, y0, s, i < p.Health ? full : empty);

			// reload bar for the second character's pulse attack
			if (second)
			{
				float ready = p.PulseReadyFraction;
				batch.Rect(new Rect(x0, y0 + 16, 85, 5), Color.Black * 0.6f);
				batch.Rect(new Rect(x0 + 1, y0 + 17, 83 * ready, 3), ready >= 1 ? new Color(0.5f, 0.9f, 1.0f, 1.0f) : new Color(0.25f, 0.5f, 0.7f, 1.0f));
			}
		}

		// boss bars
		float y = 14;
		foreach (var actor in World.All<Hostile>())
		{
			if (actor is not Hostile boss || !boss.IsBoss || !boss.Engaged || boss.IsDead)
				continue;

			float cx = bounds.Width / 2;
			UI.Text(batch, boss.Title, new Vec2(cx, y), new Vec2(0.5f, 0), Color.White);
			batch.Rect(new Rect(cx - 111, y + 22, 222, 10), Color.Black * 0.7f);
			batch.Rect(new Rect(cx - 109, y + 24, 218 * Math.Clamp(boss.Health / (float)boss.MaxHealth, 0, 1), 6), new Color(0.9f, 0.25f, 0.25f, 1.0f));
			y += 40;
		}
	}
}
