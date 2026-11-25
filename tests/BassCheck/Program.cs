using FluxTube;
Func<float,float> Detector() => new BassOnsetDetector(48000).Process;
foreach (double frequency in new[] { 35.0, 60, 80, 120, 160 })
{
    var process = Detector();
    int hits = 0, first = -1;
    for (int i = 0; i < 48000; i++)
    {
        float sample = i < 4800 ? 0 : (float)(Math.Sin((i-4800) * Math.Tau * frequency / 48000) * 0.4);
        if (process(sample) > 0) { hits++; if (first < 0) first = i; }
    }
    double delay = (first - 4800) / 48.0;
    Console.WriteLine($"Bass {frequency}Hz: {hits} hit(s), first attack {delay:F2}ms");
    if (hits != 1 || delay < 0 || delay > 25) throw new Exception("Repeated steady bass or slow response");
}
{
    var process = Detector(); int hits = 0;
    for (int i=0;i<48000;i++) if (process((float)(Math.Sin(i*Math.Tau*2000/48000)*0.6)) > 0) hits++;
    if (hits != 0) throw new Exception("Treble triggered bass shake");
    Console.WriteLine("PASS: 2kHz treble does not trigger bass shake");
}
{
    var process = Detector(); int hits = 0;
    for (int i=0;i<48000;i++)
    {
        double t = (i % 12000) / 48000.0;
        float sample = (float)(Math.Sin(t*Math.Tau*70)*Math.Exp(-t*30)*0.6);
        if(process(sample)>0) hits++;
    }
    if(hits!=4) throw new Exception($"Expected 4 bass kicks, got {hits}");
    Console.WriteLine("PASS: all 4 bass kicks detected at 240 BPM");
}

// Dense bass never falls to 45% of its mean: repeated kicks must still be detected.
foreach (int bpm in new[] { 160, 200, 240 })
{
    var process = Detector();
    int period = 48000 * 60 / bpm, warmup = 24000;
    var stamps = new List<int>();
    for (int i=0;i<warmup+period*8;i++)
    {
        double kickTime = i < warmup ? 10 : ((i-warmup)%period)/48000.0;
        double amplitude = 0.30 + 0.20 * Math.Exp(-kickTime*35);
        float sample = (float)(Math.Sin(i*Math.Tau*70/48000)*amplitude);
        if (process(sample)>0 && i>=warmup) stamps.Add(i);
    }
    Console.WriteLine($"Dense bass {bpm} BPM: {stamps.Count}/8 kicks");
    if(stamps.Count!=8) throw new Exception("Dense-bass kicks missed or duplicated");
    for(int k=0;k<8;k++)
    {
        double latency=(stamps[k]-(warmup+k*period))/48.0;
        if(latency<0 || latency>45) throw new Exception($"Dense kick latency {latency}ms");
    }
}
float previousStrength = 0;
foreach (float amplitude in new[] { 0.08f, 0.25f, 0.65f })
{
    var process = Detector();
    for(int i=0;i<4800;i++) process((float)(Math.Sin(i*Math.Tau*70/48000)*Math.Exp(-i/48000.0*18)*amplitude));
    float strength = ((BassOnsetDetector)process.Target!).AttackStrength;
    if(strength<=previousStrength+0.03f) throw new Exception("Effect strength does not follow kick intensity");
    Console.WriteLine($"Kick amplitude {amplitude:F2}: effect strength {strength:F3}");
    previousStrength = strength;
}
