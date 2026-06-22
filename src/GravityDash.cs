using System.Collections.Generic;
using UnityEngine;

// GRAVITY DASH — one-tap gravity-reversal runner.
// You auto-run forward down a neon corridor. The ONLY control is FLIP (tap / click / space / up):
// it reverses gravity so you fall onto the CEILING — tap again to drop back to the FLOOR.
// Hazard bars grow from the floor or the ceiling; flip to the OTHER surface to dash past them.
// Grab glowing energy orbs for score + combo, skim hazards for NEAR-MISS bonus. Speed ramps up.
// One hit ends the run — quick restart, chase your best.
//
// Built entirely in code (CreatePrimitive + a couple of procedural meshes) so it renders reliably
// in WebGL with engine-code stripping disabled. NO Rigidbody/colliders: the player is pure
// Transform-driven (a simple gravity integrator for the vertical axis) and every hit-test is a
// distance/band check at the pass-line (the closest approach for a forward-moving course).
// Obstacle spacing is derived from the live speed so a flip always has time to complete = always
// solvable. Coexists with Juice (sfx/bgm/particles) & AutoShot (in-engine screenshots).
public class GravityDash : MonoBehaviour
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        Application.runInBackground = true;
        var go = new GameObject("__GravityDash");
        go.AddComponent<GravityDash>();
        DontDestroyOnLoad(go);
    }

    // ---- scene refs ----
    Transform player;      // advances down the corridor (toward -Z); y = surface physics
    Transform visual;      // child that rolls 180° when running on the ceiling
    Transform cam;
    Camera camComp;
    TextMesh hudScore, hudBest, hudSpeed, hudHint, comboText, bannerText, dbg;

    // ---- spawned course objects ----
    class Bar { public Transform t; public bool ceiling; public float z, height; public bool passed; }
    class Orb { public Transform t; public float z, y; public bool taken; }
    class Deco { public Transform t; public float z; }
    readonly List<Bar> bars = new List<Bar>();
    readonly List<Orb> orbs = new List<Orb>();
    readonly List<Deco> decos = new List<Deco>();

    // ---- run state ----
    enum State { Playing, GameOver }
    State state = State.Playing;
    float playerY, vy;             // vertical position & velocity (gravity integrated)
    float gravSign = 1f;           // +1 = pulled to FLOOR, -1 = pulled to CEILING
    float rollAngle;               // visual roll (0 floor .. 180 ceiling), smoothed
    float speed = START_SPEED;     // forward m/s
    float elapsed;                 // seconds this run (drives difficulty)
    float distance;                // metres travelled (passive score)
    int score, best, combo, bestCombo;
    float comboFlash, bannerTimer, fovPunch, flipFlash;
    float genZ;                    // generation frontier (world z, negative = forward)
    float nextBarZ;                // z at/after which the next hazard bar may spawn
    bool lastBarCeiling;           // alternate surfaces for rhythm
    bool firstBar = true;          // force the very first hazard to be a ceiling bar (floor start = safe)
    int orbStreakMissed;
    bool attract = true;           // autopilot until first real input (for the demo screenshot)
    bool started;                  // becomes true on first input (gates the "TAP" hint)

    // HUD layout adapts to aspect ratio (Unity's vertical FOV is fixed => portrait is narrower)
    float hudScale = 1f, halfH = 2.7f, halfW = 4.6f;
    const float HUD_Z = 6.5f;

    // ---- geometry / tuning ----
    const float CEIL = 6.6f;          // ceiling height (floor is y=0)
    const float PLAYER_R = 0.55f;     // player half-height (clamp + hit band)
    const float START_SPEED = 17f, MAX_SPEED = 39f, RAMP_TIME = 65f;
    const float GRAV = 60f;           // flip acceleration (snappy ~0.45s traversal)
    const float HALF_W = 3.4f;        // corridor half-width (walls)
    const float SLOT = 3f;            // z granularity for generation
    const float HORIZON = 135f;       // generate this far ahead
    const float DESPAWN = 18f;        // remove this far behind
    const float NEAR_BAND = 0.9f;     // skim within this of a hazard edge = near-miss
    const float ORB_GRAB = 1.5f;      // collect an orb within this |dy|

    // debug
    bool showDbg; int dbgPass, dbgNear, dbgOrbs; float flipTimer;

    // ===================================================================== boot
    void Start()
    {
        // remove the default scene camera/light so we don't double-light or screenshot the wrong cam
        foreach (var c in FindObjectsByType<Camera>(FindObjectsSortMode.None)) Destroy(c.gameObject);
        foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None)) Destroy(l.gameObject);

        best = PlayerPrefs.GetInt("gravdash_best", 0);
        bestCombo = PlayerPrefs.GetInt("gravdash_bestcombo", 0);
        BuildEnvironment();
        BuildCamera();
        BuildPlayer();
        BuildHud();

        playerY = PLAYER_R; gravSign = 1f;
        genZ = -SLOT; nextBarZ = -22f;            // first hazard a little ahead
        while (genZ > -HORIZON) GenerateSlot();
    }

    // ===================================================================== materials / meshes
    static Material Mat(Color c, float metallic = 0f, float smooth = 0.2f, bool emissive = false, float emi = 0.7f)
    {
        var sh = Shader.Find("Universal Render Pipeline/Lit");
        if (sh == null) sh = Shader.Find("Standard");
        var m = new Material(sh);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smooth);
        if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", smooth);
        if (emissive && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c * emi);
        }
        return m;
    }

    static GameObject Prim(PrimitiveType pt, Transform parent, Vector3 lpos, Vector3 lscale, Material shared)
    {
        var g = GameObject.CreatePrimitive(pt);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);   // pure distance hit-tests
        g.transform.SetParent(parent, false);
        g.transform.localPosition = lpos;
        g.transform.localScale = lscale;
        g.GetComponent<Renderer>().sharedMaterial = shared;
        return g;
    }

    // ===================================================================== world
    Material floorMat, ceilMat, wallMat, hazMat, hazGlow, orbMat, orbMat2, playerMat, playerCore, chevMat;

    void BuildEnvironment()
    {
        floorMat   = Mat(new Color(0.07f, 0.09f, 0.16f), 0.1f, 0.55f);
        ceilMat    = Mat(new Color(0.06f, 0.08f, 0.14f), 0.1f, 0.55f);
        wallMat    = Mat(new Color(0.10f, 0.55f, 0.95f), 0f, 0.3f, true, 0.55f);
        hazMat     = Mat(new Color(1.00f, 0.22f, 0.30f), 0.1f, 0.4f, true, 0.7f);
        hazGlow    = Mat(new Color(1.00f, 0.55f, 0.30f), 0f, 0.5f, true, 1.1f);
        orbMat     = Mat(new Color(0.25f, 1.00f, 0.85f), 0f, 0.6f, true, 1.3f);
        orbMat2    = Mat(new Color(1.00f, 0.85f, 0.25f), 0f, 0.6f, true, 1.3f);
        playerMat  = Mat(new Color(0.95f, 0.97f, 1.00f), 0.3f, 0.7f, true, 0.5f);
        playerCore = Mat(new Color(0.30f, 0.85f, 1.00f), 0f, 0.8f, true, 1.5f);
        chevMat    = Mat(new Color(0.20f, 0.70f, 1.00f), 0f, 0.4f, true, 0.8f);

        var sun = new GameObject("Sun").AddComponent<Light>();
        sun.type = LightType.Directional;
        sun.color = new Color(0.85f, 0.92f, 1f);
        sun.intensity = 1.0f;
        sun.transform.rotation = Quaternion.Euler(50f, 18f, 0f);
        sun.shadows = LightShadows.None;

        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Trilight;
        RenderSettings.ambientSkyColor     = new Color(0.16f, 0.22f, 0.40f);
        RenderSettings.ambientEquatorColor = new Color(0.10f, 0.14f, 0.26f);
        RenderSettings.ambientGroundColor  = new Color(0.06f, 0.08f, 0.16f);

        // deep blue depth fog: hides spawn pop-in, blends the corridor into the dark
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = new Color(0.03f, 0.05f, 0.12f);
        RenderSettings.fogStartDistance = 45f;
        RenderSettings.fogEndDistance = 135f;

        // long static corridor: floor, ceiling, two side walls (extend far down -Z)
        float midY = CEIL * 0.5f;
        MakeStrip(new Vector3(0, -0.5f, -1800f), new Vector3(HALF_W * 2f, 1f, 4000f), floorMat);     // floor slab
        MakeStrip(new Vector3(0, CEIL + 0.5f, -1800f), new Vector3(HALF_W * 2f, 1f, 4000f), ceilMat); // ceiling slab
        MakeStrip(new Vector3(-HALF_W - 0.25f, midY, -1800f), new Vector3(0.5f, CEIL, 4000f), floorMat); // L wall
        MakeStrip(new Vector3( HALF_W + 0.25f, midY, -1800f), new Vector3(0.5f, CEIL, 4000f), floorMat); // R wall
        // emissive neon rails along floor & ceiling edges (speed + framing)
        for (int s = -1; s <= 1; s += 2)
        {
            MakeStrip(new Vector3(s * HALF_W, 0.04f, -1800f), new Vector3(0.12f, 0.08f, 4000f), wallMat);
            MakeStrip(new Vector3(s * HALF_W, CEIL - 0.04f, -1800f), new Vector3(0.12f, 0.08f, 4000f), wallMat);
        }
    }

    GameObject MakeStrip(Vector3 pos, Vector3 scale, Material m)
    {
        var g = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var col = g.GetComponent<Collider>(); if (col != null) Destroy(col);
        g.transform.position = pos; g.transform.localScale = scale;
        g.GetComponent<Renderer>().sharedMaterial = m;
        return g;
    }

    void BuildCamera()
    {
        var cgo = new GameObject("MainCamera");
        cgo.tag = "MainCamera";
        camComp = cgo.AddComponent<Camera>();
        camComp.clearFlags = CameraClearFlags.SolidColor;
        camComp.backgroundColor = new Color(0.03f, 0.05f, 0.12f);
        camComp.fieldOfView = 62f;
        camComp.farClipPlane = 300f;
        cgo.AddComponent<AudioListener>();
        cam = cgo.transform;
        cam.position = new Vector3(0, CEIL * 0.5f, 9f);
        cam.rotation = Quaternion.Euler(0f, 180f, 0f);   // look toward -Z
    }

    void BuildPlayer()
    {
        player = new GameObject("Player").transform;
        player.position = new Vector3(0, PLAYER_R, 0);
        visual = new GameObject("Visual").transform;
        visual.SetParent(player, false);

        // glowing capsule body + bright core + a little fin, "standing" on the surface
        Prim(PrimitiveType.Capsule, visual, new Vector3(0, 0f, 0), new Vector3(0.7f, 0.55f, 0.7f), playerMat);
        Prim(PrimitiveType.Sphere,  visual, new Vector3(0, 0.18f, 0.18f), new Vector3(0.34f, 0.34f, 0.34f), playerCore);
        Prim(PrimitiveType.Cube,    visual, new Vector3(0, -0.1f, -0.35f), new Vector3(0.5f, 0.15f, 0.5f), playerCore);
    }

    // ===================================================================== HUD
    TextMesh MakeText(int fontSize, float size, Color c, TextAnchor anchor)
    {
        var t = new GameObject("T").AddComponent<TextMesh>();
        t.fontSize = fontSize; t.characterSize = size; t.color = c; t.anchor = anchor;
        t.alignment = TextAlignment.Center;
        t.transform.SetParent(cam, false);
        t.transform.localRotation = Quaternion.identity;   // child of cam => reads upright
        return t;
    }

    void BuildHud()
    {
        hudScore  = MakeText(96, 0.085f, Color.white, TextAnchor.UpperLeft);
        hudBest   = MakeText(96, 0.060f, new Color(0.7f, 0.9f, 1f), TextAnchor.UpperRight);
        hudSpeed  = MakeText(96, 0.050f, new Color(0.6f, 0.85f, 1f), TextAnchor.LowerRight);
        hudHint   = MakeText(96, 0.055f, new Color(0.8f, 0.95f, 1f), TextAnchor.MiddleCenter);
        comboText = MakeText(96, 0.12f, new Color(0.3f, 1f, 0.85f), TextAnchor.MiddleCenter);
        bannerText= MakeText(96, 0.15f, Color.white, TextAnchor.MiddleCenter);
        dbg       = MakeText(96, 0.040f, new Color(0.6f, 1f, 0.7f), TextAnchor.LowerLeft);
        dbg.gameObject.SetActive(false);
        comboText.text = ""; bannerText.text = "";
        AdjustHud();
        RefreshHud();
        hudHint.text = "TAP or SPACE\nto FLIP GRAVITY";
    }

    // Recompute HUD anchors + scale from the current frustum (FOV varies w/ speed; aspect w/ window).
    void AdjustHud()
    {
        if (camComp == null) return;
        float aspect = Mathf.Max(0.3f, camComp.aspect);
        halfH = HUD_Z * Mathf.Tan(camComp.fieldOfView * 0.5f * Mathf.Deg2Rad);
        halfW = halfH * aspect;
        const float REF_HALFW = 6.0f;
        hudScale = Mathf.Clamp(halfW / REF_HALFW, 0.16f, 1.3f);
        float ix = halfW * 0.95f, iy = halfH * 0.93f;

        hudScore.transform.localPosition = new Vector3(-ix, iy, HUD_Z); hudScore.characterSize = 0.085f * hudScale;
        hudBest.transform.localPosition  = new Vector3( ix, iy, HUD_Z); hudBest.characterSize  = 0.060f * hudScale;
        hudSpeed.transform.localPosition = new Vector3( ix, -iy, HUD_Z); hudSpeed.characterSize = 0.050f * hudScale;
        hudHint.transform.localPosition  = new Vector3(0, iy * 0.6f, HUD_Z); hudHint.characterSize = 0.055f * hudScale;
        dbg.transform.localPosition      = new Vector3(-ix, -iy * 0.55f, HUD_Z); dbg.characterSize = 0.040f * hudScale;
        comboText.transform.localPosition = new Vector3(0, halfH * 0.52f, HUD_Z);
        if (comboFlash <= 0f) comboText.characterSize = 0.12f * hudScale;
    }

    void RefreshHud()
    {
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudBest)  hudBest.text  = "BEST  " + best;
        if (hudSpeed) hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
    }

    // ===================================================================== generation
    void GenerateSlot()
    {
        float z = genZ;

        // moving chevrons on floor & ceiling (cheap speed cue)
        if (((int)Mathf.Round(z / SLOT)) % 2 == 0)
        {
            SpawnChevron(z, false);
            SpawnChevron(z, true);
        }

        // hazard bars, paced by the live speed so a flip always has time to finish (= solvable)
        if (z <= nextBarZ)
        {
            bool ceiling = !lastBarCeiling;                 // alternate surfaces for rhythm
            if (Random.value < 0.28f) ceiling = lastBarCeiling; // occasional same-surface (double-up)
            if (firstBar) { ceiling = true; firstBar = false; } // first hazard safe on the starting floor
            lastBarCeiling = ceiling;
            SpawnBar(z, ceiling);

            float diff = Mathf.Clamp01(elapsed / RAMP_TIME);
            float gapTime = Mathf.Lerp(1.35f, 0.72f, diff);  // seconds between hazards (>= flip time)
            float gapZ = Mathf.Max(SLOT * 2f, speed * gapTime);
            nextBarZ = z - gapZ;

            // reward orb on the SAFE surface, just past the bar (encourages committing to the flip)
            if (Random.value < 0.7f)
                SpawnOrb(z - SLOT, ceiling ? PLAYER_R : CEIL - PLAYER_R);
        }
        else if (Random.value < 0.22f)
        {
            // free-floating orb line hugging a surface (optional risk/reward)
            bool top = Random.value < 0.5f;
            SpawnOrb(z, top ? CEIL - PLAYER_R : PLAYER_R);
        }

        genZ -= SLOT;
    }

    void SpawnBar(float z, bool ceiling)
    {
        float diff = Mathf.Clamp01(elapsed / RAMP_TIME);
        // bar height grows with difficulty; capped so the safe gap stays generous
        float h = Mathf.Lerp(2.2f, 3.4f, diff) + Random.Range(-0.2f, 0.4f);
        h = Mathf.Clamp(h, 1.8f, CEIL - 2.2f);

        var go = new GameObject(ceiling ? "ceilbar" : "floorbar");
        float baseY = ceiling ? CEIL : 0f;
        go.transform.position = new Vector3(0, baseY, z);

        float yc = ceiling ? CEIL - h * 0.5f : h * 0.5f;
        Prim(PrimitiveType.Cube, go.transform, new Vector3(0, yc, 0), new Vector3(HALF_W * 2f - 0.2f, h, 0.9f), hazMat);
        // bright danger edge at the tip (the line you must clear)
        float tipY = ceiling ? CEIL - h : h;
        Prim(PrimitiveType.Cube, go.transform, new Vector3(0, tipY, 0), new Vector3(HALF_W * 2f, 0.18f, 1.1f), hazGlow);

        bars.Add(new Bar { t = go.transform, ceiling = ceiling, z = z, height = h });
    }

    void SpawnOrb(float z, float y)
    {
        var go = new GameObject("orb");
        go.transform.position = new Vector3(0, y, z);
        var mat = Random.value < 0.5f ? orbMat : orbMat2;
        Prim(PrimitiveType.Sphere, go.transform, Vector3.zero, Vector3.one * 0.6f, mat);
        Prim(PrimitiveType.Sphere, go.transform, Vector3.zero, Vector3.one * 0.95f, mat); // soft halo (same mat)
        orbs.Add(new Orb { t = go.transform, z = z, y = y });
    }

    void SpawnChevron(float z, bool ceiling)
    {
        var go = new GameObject("chev");
        float y = ceiling ? CEIL - 0.06f : 0.06f;
        go.transform.position = new Vector3(0, y, z);
        Prim(PrimitiveType.Cube, go.transform, Vector3.zero, new Vector3(1.4f, 0.06f, 0.32f), chevMat);
        decos.Add(new Deco { t = go.transform, z = z });
    }

    // ===================================================================== input
    bool FlipPressed()
    {
        bool down = Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.UpArrow)
                 || Input.GetKeyDown(KeyCode.W) || Input.GetMouseButtonDown(0);
        for (int i = 0; i < Input.touchCount; i++)
            if (Input.GetTouch(i).phase == TouchPhase.Began) down = true;
        return down;
    }

    void Flip()
    {
        gravSign = -gravSign;
        flipFlash = 1f; fovPunch = Mathf.Max(fovPunch, 4f);
        Juice.Blip(gravSign < 0 ? 620f : 440f, 0.07f, 0.4f);
        Juice.Shake(0.08f);
        // a puff at the player when launching off a surface
        Juice.Pop(player.position - Vector3.up * gravSign * 0.4f, new Color(0.4f, 0.85f, 1f), 6);
    }

    // ===================================================================== main loop
    void Update()
    {
        float dt = Time.deltaTime;
        if (dt > 0.05f) dt = 0.05f;     // clamp big hitches so nothing tunnels

        if (Input.GetKeyDown(KeyCode.F1)) { showDbg = !showDbg; dbg.gameObject.SetActive(showDbg); }

        bool flip = FlipPressed();
        if (flip) { attract = false; started = true; if (hudHint) hudHint.text = ""; }

        if (state == State.GameOver)
        {
            if (flip) Restart();
            UpdateCamera(dt);
            SpinOrbs(dt);
            return;
        }

        // ---- autopilot until first input (drives the demo screenshot) ----
        if (attract) { if (AutoFlipWanted()) Flip(); }
        else if (flip) Flip();

        elapsed += dt;

        // ---- speed ramp ----
        float baseSpeed = Mathf.Lerp(START_SPEED, MAX_SPEED, Mathf.Clamp01(elapsed / RAMP_TIME));
        speed = Mathf.MoveTowards(speed, baseSpeed, 14f * dt);

        // ---- vertical gravity integration (toward current surface) ----
        vy -= GRAV * gravSign * dt;
        playerY += vy * dt;
        bool grounded = false;
        if (gravSign > 0f && playerY <= PLAYER_R) { playerY = PLAYER_R; vy = 0f; grounded = true; }
        else if (gravSign < 0f && playerY >= CEIL - PLAYER_R) { playerY = CEIL - PLAYER_R; vy = 0f; grounded = true; }
        else { playerY = Mathf.Clamp(playerY, PLAYER_R, CEIL - PLAYER_R); }

        // ---- advance forward ----
        distance += speed * dt;
        score = Mathf.RoundToInt(distance) + orbScore;
        float newZ = player.position.z - speed * dt;
        player.position = new Vector3(0, playerY, newZ);

        // ---- visual: roll 180° when on the ceiling; squash a touch on landing; lean with vy ----
        float targetRoll = gravSign < 0f ? 180f : 0f;
        rollAngle = Mathf.MoveTowardsAngle(rollAngle, targetRoll, 720f * dt);
        float lean = Mathf.Clamp(-vy * 0.6f, -22f, 22f) * gravSign;
        visual.localRotation = Quaternion.Euler(lean, 0f, rollAngle);

        EmitTrail(dt, grounded);
        ProcessCourse();
        Generate();
        Cull();
        SpinOrbs(dt);
        UpdateCamera(dt);
        TickHud(dt);
        if (showDbg) UpdateDbg(grounded);
    }

    int orbScore;   // accumulated orb/near-miss/clean-pass points (distance is added on top)

    void Generate() { while (genZ > player.position.z - HORIZON) GenerateSlot(); }

    void Cull()
    {
        float behind = player.position.z + DESPAWN;
        for (int i = bars.Count - 1; i >= 0; i--)
            if (bars[i].t == null || bars[i].z > behind) { if (bars[i].t) Destroy(bars[i].t.gameObject); bars.RemoveAt(i); }
        for (int i = orbs.Count - 1; i >= 0; i--)
            if (orbs[i].t == null || orbs[i].z > behind) { if (orbs[i].t) Destroy(orbs[i].t.gameObject); orbs.RemoveAt(i); }
        for (int i = decos.Count - 1; i >= 0; i--)
            if (decos[i].t == null || decos[i].z > behind) { if (decos[i].t) Destroy(decos[i].t.gameObject); decos.RemoveAt(i); }
    }

    // hit-tests at the pass-line (dz<=0 is the closest approach for a static object on a forward run)
    void ProcessCourse()
    {
        float pz = player.position.z;

        foreach (var b in bars)
        {
            if (b.passed) continue;
            if (pz <= b.z)
            {
                b.passed = true; dbgPass++;
                // hazard occupies a y-band off its surface. Crash if the player overlaps it.
                if (b.ceiling)
                {
                    float edge = CEIL - b.height;            // lowest point of the ceiling bar
                    if (playerY + PLAYER_R > edge) Crash(b);
                    else if (playerY + PLAYER_R > edge - NEAR_BAND) NearMiss(b);
                    else CleanPass();
                }
                else
                {
                    float edge = b.height;                   // highest point of the floor bar
                    if (playerY - PLAYER_R < edge) Crash(b);
                    else if (playerY - PLAYER_R < edge + NEAR_BAND) NearMiss(b);
                    else CleanPass();
                }
            }
        }

        foreach (var o in orbs)
        {
            if (o.taken) continue;
            if (pz <= o.z)
            {
                o.taken = true;
                if (Mathf.Abs(playerY - o.y) < ORB_GRAB) Collect(o);
                else { orbStreakMissed++; if (combo > 0) { combo = 0; if (comboText) comboText.text = ""; } }
            }
        }
    }

    // ===================================================================== events
    void Collect(Orb o)
    {
        dbgOrbs++;
        combo++;
        if (combo > bestCombo) bestCombo = combo;
        int gain = 50 + combo * 10;
        orbScore += gain;
        comboFlash = 1f;
        if (combo >= 2) { comboText.text = "COMBO ×" + combo; FlashCombo(); }
        Vector3 wp = new Vector3(0, o.y, o.z);
        Juice.Score(wp);
        Juice.Blip(820f + Mathf.Min(combo, 14) * 40f, 0.05f, 0.35f);
        if (o.t) Destroy(o.t.gameObject);
        RefreshHud();
    }

    void NearMiss(Bar b)
    {
        dbgNear++;
        orbScore += 25;
        Juice.Blip(1250f, 0.05f, 0.25f);
        float ey = b.ceiling ? CEIL - b.height : b.height;
        Juice.Pop(new Vector3(0, ey, b.z), new Color(1f, 0.85f, 0.4f), 6);
        FloatText("NEAR MISS +25", new Color(1f, 0.85f, 0.4f));
        RefreshHud();
    }

    void CleanPass() { orbScore += 5; }

    void Crash(Bar b)
    {
        Juice.Hit(); Juice.Shake(0.6f);
        Juice.Pop(player.position, new Color(1f, 0.4f, 0.35f), 18);
        fovPunch = Mathf.Max(fovPunch, -8f);
        GameOver();
    }

    void GameOver()
    {
        state = State.GameOver;
        bool nb = score > best;
        if (nb) { best = score; PlayerPrefs.SetInt("gravdash_best", best); }
        PlayerPrefs.SetInt("gravdash_bestcombo", bestCombo); PlayerPrefs.Save();
        Juice.Lose();

        hudScore.gameObject.SetActive(false);
        hudBest.gameObject.SetActive(false);
        hudSpeed.gameObject.SetActive(false);
        hudHint.gameObject.SetActive(false);
        comboText.gameObject.SetActive(false);
        bannerText.transform.localPosition = new Vector3(0, 0, HUD_Z);
        bannerText.characterSize = 0.092f * hudScale;
        bannerText.color = Color.white;
        bannerText.text = "CRASHED\n\nSCORE  " + score + (nb ? "\nNEW BEST!" : "\nBEST  " + best)
                        + "\nBEST COMBO ×" + bestCombo + "\n\nTAP TO RUN AGAIN";
        bannerTimer = 9999f;
    }

    void Restart()
    {
        foreach (var b in bars) if (b.t) Destroy(b.t.gameObject);
        foreach (var o in orbs) if (o.t) Destroy(o.t.gameObject);
        foreach (var d in decos) if (d.t) Destroy(d.t.gameObject);
        bars.Clear(); orbs.Clear(); decos.Clear();

        hudScore.gameObject.SetActive(true);
        hudBest.gameObject.SetActive(true);
        hudSpeed.gameObject.SetActive(true);
        comboText.gameObject.SetActive(true);
        bannerText.text = ""; comboText.text = "";

        state = State.Playing;
        playerY = PLAYER_R; vy = 0f; gravSign = 1f; rollAngle = 0f;
        speed = START_SPEED; elapsed = 0; distance = 0; orbScore = 0;
        score = 0; combo = 0; comboFlash = 0; fovPunch = 0; orbStreakMissed = 0;
        lastBarCeiling = false; firstBar = true;
        player.position = new Vector3(0, PLAYER_R, 0);
        genZ = -SLOT; nextBarZ = -22f;
        while (genZ > -HORIZON) GenerateSlot();
        RefreshHud();
    }

    // ===================================================================== autopilot (attract demo)
    bool AutoFlipWanted()
    {
        // find the nearest unpassed hazard ahead; flip in time to be on its safe surface
        Bar next = null; float nz = -9999f;
        foreach (var b in bars)
            if (!b.passed && b.z < player.position.z && b.z > nz) { nz = b.z; next = b; }
        if (next == null) return false;
        float dz = player.position.z - next.z;          // metres until the bar
        float t = dz / Mathf.Max(1f, speed);            // seconds until the bar
        bool wantCeiling = !next.ceiling;               // safe surface is the opposite one
        bool headingCeiling = gravSign < 0f;
        if (t < 0.5f && wantCeiling != headingCeiling) return true;
        return false;
    }

    // ===================================================================== feel helpers
    void FlashCombo()
    {
        comboText.color = combo >= 10 ? new Color(1f, 0.4f, 0.8f)
                        : combo >= 5  ? new Color(0.5f, 0.8f, 1f)
                                      : new Color(0.3f, 1f, 0.85f);
    }

    void FloatText(string s, Color c)
    {
        bannerText.transform.localPosition = new Vector3(0, -halfH * 0.4f, HUD_Z);
        bannerText.characterSize = 0.12f * hudScale;
        bannerText.text = s; bannerText.color = c; bannerTimer = 0.6f;
    }

    void EmitTrail(float dt, bool grounded)
    {
        trailT += dt;
        if (trailT < 0.05f) return;
        trailT = 0f;
        var q = GameObject.CreatePrimitive(PrimitiveType.Quad);
        var col = q.GetComponent<Collider>(); if (col) Destroy(col);
        // a thin horizontal motion-streak just behind the runner (reads as a speed line, not a column)
        q.transform.position = player.position + new Vector3(Random.Range(-0.1f, 0.1f), 0, 0.85f);
        q.transform.rotation = Quaternion.Euler(0, 180f, 0);
        q.transform.localScale = new Vector3(Random.Range(0.55f, 0.85f), 0.12f, 1f);
        var sh = Shader.Find("Sprites/Default"); if (sh == null) sh = Shader.Find("Unlit/Color");
        var mr = q.GetComponent<MeshRenderer>();
        mr.material = new Material(sh) { color = new Color(0.35f, 0.85f, 1f, 0.4f) };
        q.AddComponent<Streak>().Init(new Vector3(0, 0, 7f), mr);
    }
    float trailT;

    // ===================================================================== camera / scenery
    void UpdateCamera(float dt)
    {
        if (cam == null || player == null) return;
        Vector3 p = player.position;
        // sit near corridor centre, follow the player's height a little so flips feel grounded
        float camY = CEIL * 0.5f + (p.y - CEIL * 0.5f) * 0.32f;
        Vector3 want = new Vector3(0, camY, p.z + 9f);
        cam.position = Vector3.Lerp(cam.position, want, 1f - Mathf.Exp(-8f * dt));
        float lookY = CEIL * 0.5f + (p.y - CEIL * 0.5f) * 0.5f;
        Vector3 look = new Vector3(0, lookY, p.z - 14f);
        Quaternion q = Quaternion.LookRotation(look - cam.position, Vector3.up);
        cam.rotation = Quaternion.Slerp(cam.rotation, q, 1f - Mathf.Exp(-9f * dt));

        fovPunch = Mathf.Lerp(fovPunch, 0f, 6f * dt);
        float baseFov = 60f + Mathf.Clamp01((speed - START_SPEED) / (MAX_SPEED - START_SPEED)) * 14f;
        camComp.fieldOfView = Mathf.Clamp(baseFov + fovPunch, 50f, 90f);
        AdjustHud();
    }

    void SpinOrbs(float dt)
    {
        for (int i = 0; i < orbs.Count; i++)
        {
            var o = orbs[i];
            if (o.t == null) continue;
            o.t.Rotate(0f, 140f * dt, 0f, Space.World);
        }
    }

    void TickHud(float dt)
    {
        if (comboFlash > 0f)
        {
            comboFlash -= dt * 2.2f;
            if (comboText) comboText.characterSize = 0.12f * hudScale * (1f + Mathf.Max(0f, comboFlash) * 0.6f);
        }
        if (bannerTimer > 0f && bannerTimer < 9000f)
        {
            bannerTimer -= dt;
            if (bannerTimer <= 0f) { bannerText.text = ""; bannerText.color = Color.white; }
        }
        flipFlash = Mathf.Max(0f, flipFlash - dt * 3f);
        if (hudScore) hudScore.text = "SCORE  " + score;
        if (hudSpeed) hudSpeed.text = Mathf.RoundToInt(speed * 3.6f) + " km/h";
        // pulse the start hint until the player flips for the first time
        if (!started && hudHint)
        {
            float a = 0.55f + 0.45f * Mathf.Sin(elapsed * 4f);
            hudHint.color = new Color(0.8f, 0.95f, 1f, a);
        }
    }

    void UpdateDbg(bool grounded)
    {
        dbg.text = string.Format(
            "state {0}  spd {1:0.0}  grav {2:+0;-0}\ny {3:0.00}  vy {4:0.0}  grounded {5}\nscore {6} combo {7} bestC {8}\nbars {9} orbs {10} decos {11}\npass {12} near {13} got {14}\ndist {15:0} fps {16:0}  fov {17:0}\nasp {18:0.00} scale {19:0.00}",
            state, speed, (int)gravSign, playerY, vy, grounded ? 1 : 0,
            score, combo, bestCombo, bars.Count, orbs.Count, decos.Count,
            dbgPass, dbgNear, dbgOrbs, distance, 1f / Mathf.Max(0.0001f, Time.smoothDeltaTime),
            camComp != null ? camComp.fieldOfView : 0f, camComp != null ? camComp.aspect : 0f, hudScale);
    }
}

// short-lived speed streak left behind the runner (billboard quad)
public class Streak : MonoBehaviour
{
    Vector3 vel; MeshRenderer mr; float age, life = 0.4f, baseA;
    public void Init(Vector3 v, MeshRenderer r) { vel = v; mr = r; baseA = r.material.color.a; }
    void Update()
    {
        float dt = Time.deltaTime; age += dt;
        transform.position += vel * dt;
        transform.localScale *= 1f + dt * 1.2f;
        if (mr != null) { var c = mr.material.color; c.a = Mathf.Clamp01(baseA * (1f - age / life)); mr.material.color = c; }
        if (age >= life) Destroy(gameObject);
    }
}
