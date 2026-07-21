// Soak-Tests messen prozessweite Ressourcen (GC-Heap, Threads, Sockets). Parallele
// Test-Klassen verfälschen diese Messungen gegenseitig → Suite serialisieren.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
