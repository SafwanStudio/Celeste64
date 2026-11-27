
namespace Celeste64;

/// <summary>
/// Anything the players can fight: normal enemies and the bosses.
/// None of them have faces on purpose.
/// </summary>
public abstract class Hostile : Actor, IHaveModels, ICastPointShadow
{
	public int Health;
	public readonly int MaxHealth;
	public bool IsDead;
	public bool IsBoss;
	public bool Engaged;
	public bool Shielded;
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
	protected float FaceAngle = 0;
	protected float BobHeight = 1.5f;
	protected float GroundOffset = 0;
	protected Vec3 Home;
	protected float Age;
	protected float VerticalSpeed;

	private float tInvuln;
	private float tFlash;

	protected Hostile(string modelName, int health)
	{
		MaxHealth = Health = health;
		Model = new(Assets.Models[modelName]);
		Model.Flags = ModelFlags.Default;
		Model.MakeMaterialsUnique();
	}

	public bool Enraged => Health * 2 <= MaxHealth;

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

	// ---------- helpers ----------

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

	protected float GroundZBelow(Vec3 from, float fallback)
	{
		if (World.SolidRayCast(from, -Vec3.UnitZ, 600, out var hit))
			return hit.Point.Z;
		return fallback;
	}

	/// <summary>Gravity and ground snapping for walking enemies. Returns true when standing on the ground.</summary>
	protected bool GroundPhysics()
	{
		float dt = Time.Delta;
		bool hasFloor = World.SolidRayCast(Position + Vec3.UnitZ * 12, -Vec3.UnitZ, 12 + 30, out var hit);
		float targetZ = hasFloor ? hit.Point.Z + GroundOffset : float.MinValue;

		if (hasFloor && Position.Z <= targetZ + 0.6f && VerticalSpeed <= 0)
		{
			Position = new Vec3(Position.X, Position.Y, targetZ);
			VerticalSpeed = 0;
			return true;
		}

		VerticalSpeed = MathF.Max(VerticalSpeed - 500 * dt, -220);
		Position += Vec3.UnitZ * VerticalSpeed * dt;
		return false;
	}

	protected void WallPush(float radius = 6)
	{
		if (World.SolidWallCheckNearest(Position + Vec3.UnitZ * 6, radius, out var wall))
			Position += new Vec3(wall.Pushout.X, wall.Pushout.Y, 0);
	}

	protected int CountMinions(float radius)
	{
		int count = 0;
		foreach (var actor in World.All<Hostile>())
			if (actor is Hostile h && !h.IsBoss && !h.IsDead && (h.Position - Position).LengthSquared() < radius * radius)
				count++;
		return count;
	}

	protected void Summon<T>(Vec3 at) where T : Hostile, new()
	{
		var minion = new T();
		minion.Position = at;
		World.Add(minion);
		World.Request<Dust>().Init(at, Vec3.Zero, new Color(0.9f, 0.7f, 1.0f, 1.0f) * 0.6f, "circle", 22, 0.4f, 0);
	}

	protected void FireBolt(Vec3 velocity, bool homing = false, int damage = 1)
	{
		World.Add(new BossBolt(Position + CenterOffset, velocity, homing, damage));
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

	private void ContactCheck()
	{
		if (ContactDamage <= 0)
			return;

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

	/// <summary>Returns true if the hit landed (even if a shield soaked it)</summary>
	public bool Hit(Actor attacker, Vec3 direction, int damage)
	{
		if (IsDead || tInvuln > 0)
			return false;

		var dir = direction.XY();
		dir = dir.LengthSquared() > 0.001f ? dir.Normalized() : Vec2.UnitX;
		tInvuln = 0.45f;

		if (Shielded)
		{
			// the shield soaks everything
			World.HitStun = 0.04f;
			Audio.Play(Sfx.sfx_dashcrystal, Position);
			Burst(8, new Color(0.7f, 0.95f, 1.0f, 1.0f), 45);
			return true;
		}

		Health -= damage;
		tFlash = 0.25f;
		Knock = new Vec3(dir * KnockSpeed, 22);

		World.HitStun = 0.07f;
		Audio.Play(Sfx.sfx_land, Position);
		Burst(14, new Color(1.0f, 0.9f, 0.6f, 1.0f), 55);
		OnHit();

		if (Health <= 0)
			Die();
		return true;
	}

	protected void Die()
	{
		if (IsDead)
			return;
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
			Matrix.CreateRotationZ(FaceAngle + Age * SpinSpeed) *
			Matrix.CreateTranslation(Vec3.UnitZ * MathF.Sin(Age * 2.2f) * BobHeight);

		bool flash = tFlash > 0 && ((int)(tFlash * 30) % 2 == 0);
		foreach (var mat in Model.Materials)
			mat.Color = flash ? new Color(1.0f, 0.35f, 0.35f, 1.0f) : TintColor();

		populate.Add((this, Model));
	}
}

// =====================================================================
//  Normal enemies
// =====================================================================

/// <summary>Drifter: floats toward the closest player in a weaving line</summary>
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
		var target = NearestPlayer(90);
		if (target != null)
		{
			var to = target.Position + Vec3.UnitZ * 5 - Position;
			if (to.LengthSquared() > 1)
			{
				var dir = to.Normalized();
				var side = new Vec3(-dir.Y, dir.X, 0) * MathF.Sin(Age * 3.0f) * 0.7f;
				Position += (dir + side).Normalized() * 24 * Time.Delta;
			}
			KeepHeight(6, 45);
		}
		else
		{
			ReturnHome(10);
		}
	}
}

/// <summary>Crawler: walks on the ground, crouches and then pounces</summary>
public class Crawler : Hostile
{
	private Vec2 wander = Vec2.UnitX;
	private float tWander;
	private float tPounce;
	private float tWind;
	private float tCool = 1.0f;
	private bool windup;
	private Vec2 pounceDir = Vec2.UnitX;

	public Crawler() : base("crawler", 3)
	{
		HitRadius = 5;
		ContactRadius = 6;
		SpinSpeed = 0;
		BobHeight = 0;
		KnockSpeed = 50;
	}

	protected override float Squash() => windup ? 0.8f : 1.0f;
	protected override Color TintColor() => windup ? new Color(1.0f, 0.7f, 0.6f, 1.0f) : Color.White;

	protected override void AI()
	{
		float dt = Time.Delta;
		bool grounded = GroundPhysics();
		if (tCool > 0)
			tCool -= dt;

		if (tPounce > 0)
		{
			tPounce -= dt;
			Position += new Vec3(pounceDir * 85 * dt, 0);
			WallPush();
			return;
		}

		var target = NearestPlayer(95);
		if (target != null)
		{
			var to = (target.Position - Position).XY();
			float dist = to.Length();
			var dir = dist > 0.01f ? to / dist : Vec2.UnitX;
			FaceAngle = MathF.Atan2(dir.Y, dir.X);

			if (windup)
			{
				tWind -= dt;
				if (tWind <= 0)
				{
					windup = false;
					tPounce = 0.45f;
					pounceDir = dir;
					VerticalSpeed = 110;
					tCool = 1.8f;
					Audio.Play(Sfx.sfx_jump, Position);
				}
				return;
			}

			if (dist < 34 && tCool <= 0 && grounded)
			{
				windup = true;
				tWind = 0.35f;
				return;
			}

			Position += new Vec3(dir * 32 * dt, 0);
		}
		else
		{
			windup = false;
			tWander -= dt;
			if (tWander <= 0)
			{
				tWander = World.Rng.Float(1.5f, 3.0f);
				float a = World.Rng.Float() * MathF.Tau;
				wander = new Vec2(MathF.Cos(a), MathF.Sin(a));
				var toHome = (Home - Position).XY();
				if (toHome.Length() > 60)
					wander = toHome.Normalized();
			}
			Position += new Vec3(wander * 10 * dt, 0);
		}

		WallPush();
	}
}

/// <summary>Spitter: keeps its distance and shoots slow bolts</summary>
public class Spitter : Hostile
{
	private float tFire = 1.5f;

	public Spitter() : base("spitter", 2)
	{
		HitRadius = 5;
		ContactRadius = 6;
		SpinSpeed = 1.2f;
		BobHeight = 2;
		KnockSpeed = 50;
	}

	protected override Color TintColor() => tFire < 0.5f ? new Color(1.0f, 1.0f, 0.6f, 1.0f) : Color.White;

	protected override void AI()
	{
		var target = NearestPlayer(150);
		if (target == null)
		{
			ReturnHome(8);
			return;
		}

		var to = target.Position + Vec3.UnitZ * 5 - Position;
		float dist = to.Length();
		var dir = dist > 0.01f ? to / dist : Vec3.UnitX;

		if (dist < 55)
			Position -= dir * 20 * Time.Delta;
		else if (dist > 90)
			Position += dir * 16 * Time.Delta;
		KeepHeight(8, 50);

		tFire -= Time.Delta;
		if (tFire <= 0)
		{
			tFire = 2.4f;
			Audio.Play(Sfx.sfx_dash_pink, Position);
			FireBolt(dir * 38);
		}
	}
}

/// <summary>Lancer: circles around a player, then lunges in a straight line</summary>
public class Lancer : Hostile
{
	private enum Phase { Circle, Windup, Lunge, Recover }

	private Phase phase = Phase.Circle;
	private float tPhase;
	private float strafe = Random.Shared.NextSingle() > 0.5f ? 1 : -1;
	private Vec3 lungeDir = Vec3.UnitX;

	public Lancer() : base("lancer", 2)
	{
		HitRadius = 5;
		ContactRadius = 6;
		SpinSpeed = 0;
		BobHeight = 1;
		KnockSpeed = 60;
	}

	protected override Color TintColor() => phase == Phase.Windup ? new Color(1.0f, 0.9f, 0.4f, 1.0f) : Color.White;

	protected override void AI()
	{
		var target = NearestPlayer(130);
		if (target == null)
		{
			phase = Phase.Circle;
			tPhase = 0;
			ReturnHome(10);
			return;
		}

		tPhase += Time.Delta;
		var tp = target.Position + Vec3.UnitZ * 5;
		var to = tp - Position;
		var dir = to.LengthSquared() > 1 ? to.Normalized() : lungeDir;

		switch (phase)
		{
			case Phase.Circle:
			{
				FaceAngle = MathF.Atan2(dir.Y, dir.X);
				var rel = (Position - tp).XY();
				float dist = rel.Length();
				var radial = dist > 0.01f ? rel / dist : Vec2.UnitX;
				var tangent = new Vec2(-radial.Y, radial.X) * strafe;
				var move = tangent * 30 - radial * (dist - 48) * 0.8f;
				Position += new Vec3(move * Time.Delta, (tp.Z - Position.Z) * Time.Delta);
				KeepHeight(6, 45);
				if (tPhase > 2.0f)
				{
					phase = Phase.Windup;
					tPhase = 0;
				}
				break;
			}

			case Phase.Windup:
				FaceAngle = MathF.Atan2(dir.Y, dir.X);
				lungeDir = dir;
				if (tPhase > 0.6f)
				{
					phase = Phase.Lunge;
					tPhase = 0;
					Audio.Play(Sfx.sfx_dash_red, Position);
				}
				break;

			case Phase.Lunge:
				Position += lungeDir * 135 * Time.Delta;
				KeepHeight(6, 45);
				if (tPhase > 0.45f)
				{
					phase = Phase.Recover;
					tPhase = 0;
				}
				break;

			case Phase.Recover:
				if (tPhase > 1.0f)
				{
					phase = Phase.Circle;
					tPhase = 0;
					strafe = -strafe;
				}
				break;
		}
	}
}

/// <summary>Brute: slow, tanky, hits hard and stomps the ground</summary>
public class Brute : Hostile
{
	private float tStomp;
	private float tCool = 2.0f;
	private bool stomping;
	private Vec2 wander = Vec2.UnitX;
	private float tWander;

	public Brute() : base("brute", 7)
	{
		HitRadius = 8;
		ContactRadius = 9;
		ContactDamage = 2;
		KnockSpeed = 18;
		ModelScale = 1.15f;
		BobHeight = 0;
		SpinSpeed = 0;
	}

	protected override float Squash() => stomping ? 0.85f + MathF.Sin(Age * 40) * 0.04f : 1.0f;
	protected override Color TintColor() => stomping ? new Color(1.0f, 0.65f, 0.6f, 1.0f) : Color.White;

	protected override void AI()
	{
		float dt = Time.Delta;
		bool grounded = GroundPhysics();
		if (tCool > 0)
			tCool -= dt;

		if (stomping)
		{
			tStomp -= dt;
			if (tStomp <= 0)
			{
				stomping = false;
				tCool = 3.5f;
				Audio.Play(Sfx.sfx_land, Position);
				World.HitStun = 0.05f;
				World.Add(new Shockwave(Position + Vec3.UnitZ * 1, 75, 85, 11));
			}
			return;
		}

		var target = NearestPlayer(110);
		if (target != null)
		{
			var to = (target.Position - Position).XY();
			float dist = to.Length();
			var dir = dist > 0.01f ? to / dist : Vec2.UnitX;
			FaceAngle = MathF.Atan2(dir.Y, dir.X);

			if (dist < 44 && tCool <= 0 && grounded)
			{
				stomping = true;
				tStomp = 0.8f;
				return;
			}
			Position += new Vec3(dir * 15 * dt, 0);
		}
		else
		{
			tWander -= dt;
			if (tWander <= 0)
			{
				tWander = World.Rng.Float(2.0f, 4.0f);
				float a = World.Rng.Float() * MathF.Tau;
				wander = new Vec2(MathF.Cos(a), MathF.Sin(a));
				var toHome = (Home - Position).XY();
				if (toHome.Length() > 50)
					wander = toHome.Normalized();
			}
			Position += new Vec3(wander * 6 * dt, 0);
		}

		WallPush(8);
	}
}

/// <summary>Bomber: floats toward you, then flashes and explodes</summary>
public class Bomber : Hostile
{
	private bool fuse;
	private float tFuse;

	public Bomber() : base("bomber", 1)
	{
		HitRadius = 5;
		ContactRadius = 5;
		ContactDamage = 0;
		SpinSpeed = 1;
		BobHeight = 1.5f;
		KnockSpeed = 40;
	}

	protected override float Squash() => fuse ? 1.0f + MathF.Sin(Age * 36) * 0.12f : 1.0f;
	protected override Color TintColor()
		=> fuse && ((int)(Age * 14) % 2 == 0) ? new Color(1.0f, 0.5f, 0.5f, 1.0f) : Color.White;

	protected override void AI()
	{
		var target = NearestPlayer(100);
		if (target == null)
		{
			fuse = false;
			ReturnHome(8);
			return;
		}

		var to = target.Position + Vec3.UnitZ * 5 - Position;
		float dist = to.Length();
		var dir = dist > 0.01f ? to / dist : Vec3.UnitX;

		if (!fuse)
		{
			Position += dir * 20 * Time.Delta;
			KeepHeight(6, 45);
			if (dist < 24)
			{
				fuse = true;
				tFuse = 1.1f;
				Audio.Play(Sfx.sfx_dash_red, Position);
			}
		}
		else
		{
			Position += dir * 6 * Time.Delta;
			tFuse -= Time.Delta;
			if (tFuse <= 0)
				Explode();
		}
	}

	private void Explode()
	{
		Burst(30, new Color(1.0f, 0.6f, 0.3f, 1.0f), 80);
		World.Request<Dust>().Init(Position, Vec3.Zero, new Color(1.0f, 0.6f, 0.3f, 1.0f) * 0.6f, "circle", 70, 0.45f, 0);
		World.Request<Dust>().Init(Position, Vec3.Zero, Color.White * 0.5f, "circle", 40, 0.3f, 0);
		Audio.Play(Sfx.sfx_glassbreak, Position);

		foreach (var actor in World.All<Player>())
			if (actor is Player p && !p.Dead && (p.Position + Vec3.UnitZ * 5 - Position).LengthSquared() < 30 * 30)
				p.Hurt(Position, 2);

		Die();
	}
}

// =====================================================================
//  Bosses
// =====================================================================

/// <summary>WARDEN: charges, leaps and slams the ground, and calls helpers when enraged</summary>
public class BossCharger : Hostile
{
	private enum Phase { Idle, Windup, Charge, Recover, SlamRise, SlamHold, SlamDrop, Summon }

	private Phase phase = Phase.Idle;
	private float tPhase;
	private Vec3 chargeDir = Vec3.UnitX;
	private int attackIndex;
	private int extraCharges;
	private float groundZ;

	public BossCharger() : base("boss1", 16)
	{
		IsBoss = true;
		Title = "WARDEN";
		HitRadius = 13;
		ContactRadius = 14;
		SpinSpeed = 0.6f;
		BobHeight = 2;
		KnockSpeed = 30;
	}

	protected override float Squash()
		=> (phase == Phase.Windup || phase == Phase.SlamHold || phase == Phase.Summon) ? 1.0f + MathF.Sin(Age * 45) * 0.07f : 1.0f;

	protected override Color TintColor()
	{
		if (phase == Phase.Windup || phase == Phase.SlamHold)
			return new Color(1.0f, 0.55f, 0.55f, 1.0f);
		return Enraged ? new Color(1.0f, 0.8f, 0.6f, 1.0f) : Color.White;
	}

	private float SpeedMult => Enraged ? 1.3f : 1.0f;

	private void Next()
	{
		phase = Phase.Idle;
		tPhase = 0;
	}

	protected override void AI()
	{
		var target = NearestPlayer(Engaged ? 420 : 200);
		Engaged = target != null;
		if (target == null)
		{
			Next();
			ReturnHome(14);
			return;
		}

		tPhase += Time.Delta;
		var to = target.Position + Vec3.UnitZ * 5 - Position;
		var dir = to.LengthSquared() > 1 ? to.Normalized() : chargeDir;
		var flat = new Vec3(dir.X, dir.Y, 0);

		switch (phase)
		{
			case Phase.Idle:
				ContactDamage = 1;
				if ((Position - Home).XY().Length() > 260)
					ReturnHome(30);
				else
					Position += flat * 14 * SpeedMult * Time.Delta;
				KeepHeight(10, 30);
				if (tPhase > (Enraged ? 1.4f : 2.0f))
				{
					attackIndex++;
					int pick = attackIndex % 4;
					tPhase = 0;
					if (pick == 0 || pick == 2)
						phase = Phase.Windup;
					else if (pick == 1)
						phase = Phase.SlamRise;
					else
						phase = Enraged ? Phase.Summon : Phase.SlamRise;
				}
				break;

			case Phase.Windup:
				chargeDir = dir;
				if (tPhase > (Enraged ? 0.65f : 0.9f))
				{
					phase = Phase.Charge;
					tPhase = 0;
					Audio.Play(Sfx.sfx_dash_red, Position);
				}
				break;

			case Phase.Charge:
				ContactDamage = 2;
				Position += chargeDir * 105 * SpeedMult * Time.Delta;
				KeepHeight(10, 30);
				if (tPhase > 0.7f || (Position - Home).XY().Length() > 320)
				{
					phase = Phase.Recover;
					tPhase = 0;
					extraCharges = Enraged ? 1 : 0;
				}
				break;

			case Phase.Recover:
				ContactDamage = 1;
				if (tPhase > (extraCharges > 0 ? 0.5f : 1.4f))
				{
					if (extraCharges > 0)
					{
						extraCharges--;
						phase = Phase.Windup;
						tPhase = 0;
					}
					else
					{
						Next();
					}
				}
				break;

			case Phase.SlamRise:
				// leap up and drift above the target
				Position += new Vec3(flat.X, flat.Y, 0) * 70 * SpeedMult * Time.Delta + Vec3.UnitZ * 70 * Time.Delta;
				if (tPhase > 0.55f)
				{
					phase = Phase.SlamHold;
					tPhase = 0;
				}
				break;

			case Phase.SlamHold:
				Position += new Vec3(flat.X, flat.Y, 0) * 40 * Time.Delta;
				if (tPhase > 0.3f)
				{
					phase = Phase.SlamDrop;
					tPhase = 0;
					groundZ = GroundZBelow(Position, 0);
				}
				break;

			case Phase.SlamDrop:
				ContactDamage = 2;
				Position -= Vec3.UnitZ * 230 * Time.Delta;
				if (Position.Z <= groundZ + 12 || tPhase > 1.5f)
				{
					Position = new Vec3(Position.X, Position.Y, groundZ + 12);
					Audio.Play(Sfx.sfx_land, Position);
					World.HitStun = 0.08f;
					Burst(30, new Color(1.0f, 0.7f, 0.4f, 1.0f), 80);
					World.Add(new Shockwave(new Vec3(Position.X, Position.Y, groundZ + 1), 150, 95, 10));
					phase = Phase.Recover;
					tPhase = 0;
					extraCharges = 0;
				}
				break;

			case Phase.Summon:
				ContactDamage = 1;
				if (tPhase > 1.0f)
				{
					if (CountMinions(260) < 4)
					{
						Summon<Lancer>(Position + new Vec3(30, 0, 0));
						Summon<Lancer>(Position + new Vec3(-30, 0, 0));
					}
					phase = Phase.Recover;
					tPhase = 0;
					extraCharges = 0;
				}
				break;
		}
	}
}

/// <summary>KEEPER: shoots volleys and homing orbs, blinks around and raises a shield</summary>
public class BossShooter : Hostile
{
	private enum Move { Fan, Fan2, Ring, Homing, Blink, Shield }

	private static readonly Move[] Pattern = [Move.Fan, Move.Fan2, Move.Ring, Move.Homing, Move.Blink, Move.Shield];

	private int index;
	private float tNext = 1.6f;
	private float orbit;
	private bool blinking;
	private float tBlink;
	private Vec3 blinkTarget;
	private float tShield;

	public BossShooter() : base("boss2", 20)
	{
		IsBoss = true;
		Title = "KEEPER";
		HitRadius = 14;
		ContactRadius = 15;
		SpinSpeed = 0.9f;
		BobHeight = 2.5f;
		KnockSpeed = 25;
	}

	protected override float Squash() => blinking ? 1.0f + MathF.Sin(Age * 50) * 0.08f : 1.0f;
	protected override Color TintColor()
	{
		if (Shielded)
			return new Color(0.6f, 1.0f, 1.0f, 1.0f);
		return Enraged ? new Color(1.0f, 0.85f, 0.85f, 1.0f) : Color.White;
	}

	protected override void AI()
	{
		var target = NearestPlayer(Engaged ? 420 : 220);
		Engaged = target != null;
		if (target == null)
		{
			Shielded = false;
			blinking = false;
			ReturnHome(12);
			return;
		}

		float dt = Time.Delta;

		if (Shielded)
		{
			tShield -= dt;
			if (World.Rng.Float() < 0.4f)
				World.Request<Dust>().Init(Position + new Vec3(World.Rng.Float(-14, 14), World.Rng.Float(-14, 14), World.Rng.Float(-8, 14)), Vec3.Zero, new Color(0.6f, 1.0f, 1.0f, 1.0f), "particle-star", 4, 0.3f, 0);
			if (tShield <= 0)
				Shielded = false;
		}

		if (blinking)
		{
			tBlink -= dt;
			if (World.Rng.Float() < 0.7f)
				World.Request<Dust>().Init(blinkTarget + new Vec3(World.Rng.Float(-8, 8), World.Rng.Float(-8, 8), 0), Vec3.Zero, new Color(0.6f, 0.9f, 1.0f, 1.0f) * 0.7f, "circle", 18, 0.25f, 0);
			if (tBlink <= 0)
			{
				blinking = false;
				Burst(24, new Color(0.7f, 0.95f, 1.0f, 1.0f), 70);
				Position = blinkTarget;
				Burst(24, new Color(0.7f, 0.95f, 1.0f, 1.0f), 70);
				Audio.Play(Sfx.sfx_dash_pink, Position);
				FanVolley(target, 3);
			}
			return;
		}

		// slowly circle around the home position
		orbit += dt * 0.45f;
		var goal = Home + new Vec3(MathF.Cos(orbit) * 70, MathF.Sin(orbit) * 70, 0);
		var d = goal - Position;
		float dist = d.Length();
		if (dist > 1)
			Position += d.Normalized() * MathF.Min(40, dist * 2) * dt;

		tNext -= dt;
		if (tNext <= 0)
		{
			DoMove(Pattern[index % Pattern.Length], target);
			index++;
			tNext = Enraged ? 1.5f : 2.2f;
		}
	}

	private Vec3 AimAt(Player target)
	{
		var aim = target.Position + Vec3.UnitZ * 5 - Position;
		return aim.LengthSquared() > 1 ? aim.Normalized() : Vec3.UnitX;
	}

	private void FanVolley(Player target, int count)
	{
		var aim = AimAt(target);
		int half = count / 2;
		for (int i = -half; i <= half; i++)
		{
			float a = i * 0.3f;
			float c = MathF.Cos(a), s = MathF.Sin(a);
			FireBolt(new Vec3(aim.X * c - aim.Y * s, aim.X * s + aim.Y * c, aim.Z) * 52);
		}
	}

	private void DoMove(Move move, Player target)
	{
		switch (move)
		{
			case Move.Fan:
				Audio.Play(Sfx.sfx_dash_pink, Position);
				FanVolley(target, Enraged ? 5 : 3);
				break;

			case Move.Fan2:
				Audio.Play(Sfx.sfx_dash_pink, Position);
				FanVolley(target, 3);
				FanVolley(target, 5);
				break;

			case Move.Ring:
			{
				Audio.Play(Sfx.sfx_dash_pink, Position);
				int count = Enraged ? 14 : 10;
				for (int i = 0; i < count; i++)
				{
					float a = i / (float)count * MathF.Tau;
					FireBolt(new Vec3(MathF.Cos(a), MathF.Sin(a), 0) * 42);
				}
				break;
			}

			case Move.Homing:
			{
				Audio.Play(Sfx.sfx_dash_red, Position);
				var aim = AimAt(target);
				FireBolt(aim * 30, true, 2);
				if (Enraged)
					FireBolt(new Vec3(-aim.Y, aim.X, 0) * 30, true, 2);
				break;
			}

			case Move.Blink:
			{
				float a = World.Rng.Float() * MathF.Tau;
				float r = World.Rng.Float(70, 110);
				blinkTarget = Home + new Vec3(MathF.Cos(a) * r, MathF.Sin(a) * r, 0);
				blinking = true;
				tBlink = 0.9f;
				break;
			}

			case Move.Shield:
				Shielded = true;
				tShield = 3.2f;
				tNext = 3.2f;
				Audio.Play(Sfx.sfx_dashcrystal, Position);
				break;
		}
	}
}

/// <summary>TEMPEST: spiral bullet patterns, bomber swarms, shock pulses and sweeping dives</summary>
public class BossSpinner : Hostile
{
	private enum Move { Spiral, Summon, Pulse, Sweep }

	private static readonly Move[] Pattern = [Move.Spiral, Move.Summon, Move.Pulse, Move.Sweep];

	private int index;
	private float tNext = 2.0f;
	private bool spiraling;
	private float tSpiral;
	private float tShot;
	private float spiralAngle;
	private bool sweeping;
	private float tSweep;
	private Vec3 sweepDir = Vec3.UnitX;

	public BossSpinner() : base("boss3", 26)
	{
		IsBoss = true;
		Title = "TEMPEST";
		HitRadius = 17;
		ContactRadius = 18;
		SpinSpeed = 1.6f;
		BobHeight = 2;
		KnockSpeed = 20;
	}

	protected override Color TintColor() => Enraged ? new Color(1.0f, 0.75f, 0.9f, 1.0f) : Color.White;

	protected override void AI()
	{
		var target = NearestPlayer(Engaged ? 420 : 260);
		Engaged = target != null;
		if (target == null)
		{
			spiraling = false;
			sweeping = false;
			ReturnHome(14);
			return;
		}

		float dt = Time.Delta;

		if (spiraling)
		{
			tSpiral -= dt;
			tShot -= dt;
			if (tShot <= 0)
			{
				tShot = Enraged ? 0.08f : 0.11f;
				spiralAngle += 0.55f;
				int arms = Enraged ? 3 : 2;
				for (int i = 0; i < arms; i++)
				{
					float a = spiralAngle + i * MathF.Tau / arms;
					FireBolt(new Vec3(MathF.Cos(a), MathF.Sin(a), 0) * 36);
				}
			}
			if (tSpiral <= 0)
				spiraling = false;
			return;
		}

		if (sweeping)
		{
			ContactDamage = 2;
			tSweep -= dt;
			Position += sweepDir * 100 * dt;
			if (tSweep <= 0)
			{
				sweeping = false;
				ContactDamage = 1;
			}
			return;
		}

		ContactDamage = 1;
		ReturnHomeSoft();

		tNext -= dt;
		if (tNext <= 0)
		{
			DoMove(Pattern[index % Pattern.Length], target);
			index++;
			tNext = Enraged ? 2.0f : 3.0f;
		}
	}

	private void ReturnHomeSoft()
	{
		var d = Home - Position;
		float dist = d.Length();
		if (dist > 2)
			Position += d.Normalized() * MathF.Min(30, dist * 2) * Time.Delta;
	}

	private void DoMove(Move move, Player target)
	{
		switch (move)
		{
			case Move.Spiral:
				spiraling = true;
				tSpiral = 4.2f;
				tShot = 0;
				Audio.Play(Sfx.sfx_dash_pink, Position);
				break;

			case Move.Summon:
				Audio.Play(Sfx.sfx_dashcrystal, Position);
				if (CountMinions(300) < 6)
				{
					for (int i = 0; i < 3; i++)
					{
						float a = i / 3.0f * MathF.Tau + World.Rng.Float();
						Summon<Bomber>(Position + new Vec3(MathF.Cos(a) * 40, MathF.Sin(a) * 40, 0));
					}
				}
				break;

			case Move.Pulse:
			{
				Audio.Play(Sfx.sfx_land, Position);
				float groundZ = GroundZBelow(Position, Position.Z - 20);
				World.Add(new Shockwave(new Vec3(Position.X, Position.Y, groundZ + 1), 140, 70, 10));
				int count = Enraged ? 16 : 12;
				for (int i = 0; i < count; i++)
				{
					float a = i / (float)count * MathF.Tau;
					FireBolt(new Vec3(MathF.Cos(a), MathF.Sin(a), 0) * 40);
				}
				break;
			}

			case Move.Sweep:
			{
				var to = target.Position + Vec3.UnitZ * 5 - Position;
				sweepDir = to.LengthSquared() > 1 ? to.Normalized() : Vec3.UnitX;
				sweeping = true;
				tSweep = 0.9f;
				Audio.Play(Sfx.sfx_dash_red, Position);
				break;
			}
		}
	}
}

// =====================================================================
//  Projectiles and effects
// =====================================================================

/// <summary>Projectile used by the bosses and spitters. Can home in on players.</summary>
public class BossBolt : Actor, IHaveModels
{
	private readonly SkinnedModel model;
	private Vec3 velocity;
	private readonly bool homing;
	private readonly int damage;
	private readonly float speed;
	private float life;

	public BossBolt(Vec3 at, Vec3 velocity, bool homing = false, int damage = 1)
	{
		Position = at;
		this.velocity = velocity;
		this.homing = homing;
		this.damage = damage;
		speed = velocity.Length();
		life = homing ? 4.5f : 5.0f;
		UpdateOffScreen = true;
		LocalBounds = new BoundingBox(Vec3.Zero, 4);
		model = new(Assets.Models["bolt"]);
		model.Flags = ModelFlags.Default;
	}

	public override void Update()
	{
		if (Destroying)
			return;

		if (homing)
		{
			Player? best = null;
			float bestDist = 300 * 300;
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
			if (best != null)
			{
				var desired = (best.Position + Vec3.UnitZ * 5 - Position).Normalized() * speed;
				velocity = Vec3.Lerp(velocity, desired, 1 - MathF.Exp(-1.8f * Time.Delta));
			}
		}

		Position += velocity * Time.Delta;
		life -= Time.Delta;

		if (World.Rng.Float() < 0.5f)
			World.Request<Dust>().Init(Position, Vec3.Zero, homing ? new Color(0.9f, 0.5f, 1.0f, 1.0f) : new Color(1.0f, 0.6f, 0.3f, 1.0f), "circle", homing ? 8 : 5, 0.25f, 0);

		foreach (var actor in World.All<Player>())
		{
			if (actor is not Player p || p.Dead)
				continue;

			if ((p.Position + Vec3.UnitZ * 5 - Position).LengthSquared() < 7 * 7 && p.Hurt(Position, damage))
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
		model.Transform = Matrix.CreateScale(homing ? 1.6f : 1.0f) * Matrix.CreateRotationZ(World.GeneralTimer * 6);
		populate.Add((this, model));
	}
}

/// <summary>Ring that spreads along the ground. Jump over it.</summary>
public class Shockwave : Actor
{
	private readonly float maxRadius;
	private readonly float speed;
	private readonly float height;
	private float radius;
	private readonly HashSet<Player> alreadyHit = [];

	public Shockwave(Vec3 center, float maxRadius, float speed, float height)
	{
		Position = center;
		this.maxRadius = maxRadius;
		this.speed = speed;
		this.height = height;
		UpdateOffScreen = true;
		LocalBounds = new BoundingBox(Vec3.Zero, 4);
	}

	public override void Update()
	{
		if (Destroying)
			return;

		radius += speed * Time.Delta;

		int count = (int)MathF.Min(36, MathF.Max(10, radius * 0.45f));
		for (int i = 0; i < count; i++)
		{
			float a = (i + World.Rng.Float()) / count * MathF.Tau;
			World.Request<Dust>().Init(
				Position + new Vec3(MathF.Cos(a) * radius, MathF.Sin(a) * radius, 1.5f),
				Vec3.Zero, new Color(1.0f, 0.8f, 0.5f, 1.0f), "particle-star", 3.5f, 0.22f, 0);
		}

		foreach (var actor in World.All<Player>())
		{
			if (actor is not Player p || p.Dead || alreadyHit.Contains(p))
				continue;

			float dist = (p.Position - Position).XY().Length();
			if (MathF.Abs(dist - radius) < 6 && p.Position.Z < Position.Z + height)
			{
				if (p.Hurt(Position, 1))
					alreadyHit.Add(p);
			}
		}

		if (radius > maxRadius)
			World.Destroy(this);
	}
}

// =====================================================================
//  HUD
// =====================================================================

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
