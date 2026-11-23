namespace F10Recorder;

// Profile-local capability sharing only. Each peer keeps its own ledger lock
// session; this holder never stores file sizes or producer-thread evidence.
internal sealed class ReplayReservationObservations
{
    Registration current;

    internal sealed class Registration
    {
        readonly ReplayReservationObservations source;
        readonly Func<Registration, ReplayVolumeClaim, string, string, ReplayReservationFact> observe;
        internal readonly string Owner;
        int active = 1;
        int released;
        internal bool Active => Volatile.Read(ref active) != 0;
        internal bool Released => Volatile.Read(ref released) != 0;
        internal void Revoke() => Interlocked.Exchange(ref active, 0);
        internal void MarkReleased() => Interlocked.Exchange(ref released, 1);

        internal Registration(ReplayReservationObservations source, string owner,
            Func<Registration, ReplayVolumeClaim, string, string, ReplayReservationFact> observe)
        { this.source = source; Owner = owner; this.observe = observe; }

        internal ReplayReservationFact Observe(ReplayVolumeClaim claim, string anchor, string identity)
        {
            RequireActive();
            var fact = observe(this, claim, anchor, identity);
            RequireActive();
            return fact;
        }

        internal void RequireActive()
        {
            if (!Active) throw new IOException("Owned reservation observation was revoked.");
        }

        // Deliberately uncoordinated fault injection; production revocation is
        // performed only by ReplayVolumeReservations while owning its mutex.
        internal void RevokeForChecks()
        {
            Revoke();
            // An old close must never remove a later request's registration.
            Interlocked.CompareExchange(ref source.current, null, this);
        }
    }

    internal Registration Register(string owner,
        Func<Registration, ReplayVolumeClaim, string, string, ReplayReservationFact> observe)
    {
        if (!Guid.TryParseExact(owner, "N", out _) || observe == null)
            throw new IOException("Owned reservation observation needs its exact request owner.");
        var registration = new Registration(this, owner, observe);
        if (Interlocked.CompareExchange(ref current, registration, null) != null)
            throw new IOException("Another request still owns reservation observation.");
        return registration;
    }

    internal ReplayReservationFact Observe(string owner, ReplayVolumeClaim claim, string anchor, string identity)
    {
        var registration = Volatile.Read(ref current);
        // Other processes/owners continue through the existing checking path.
        return registration == null || registration.Owner != owner ? null
            : registration.Observe(claim, anchor, identity);
    }

    // Caller owns the authority ledger mutex. Keep a revoked registration
    // attached until durable release succeeds, blocking replacement on failure.
    internal void Revoke(string owner)
    {
        var registration = Volatile.Read(ref current);
        if (registration?.Owner == owner) registration.Revoke();
    }

    internal void Released(string owner)
    {
        var registration = Volatile.Read(ref current);
        if (registration?.Owner == owner)
        {
            if (registration.Active) throw new IOException("Reservation release precedes observation revocation.");
            registration.MarkReleased();
            Interlocked.CompareExchange(ref current, null, registration);
        }
    }
}
