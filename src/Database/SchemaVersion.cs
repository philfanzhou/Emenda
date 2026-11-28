namespace Emenda.Database;

// The managed source tables, routines and triggers were retired together in version 19.
internal readonly record struct SchemaVersion(int Number)
{
    public bool ManagedIntake => Number is >= 6 and < 19;
    public bool Targets => Number is >= 7 and < 19;
    public bool Returns => Number is >= 8 and < 19;
    public bool Handoffs => Number is >= 9 and < 19;
    public bool Archive => Number >= 11;
    public bool LegacyReturns => Number is >= 12 and < 19;
    public bool LayoutJobs => Number is >= 13 and < 19;
    public bool Extractions => Number >= 16;
    public bool Corrections => Number >= 20;
    public bool Deletion => Number >= 22;
    public bool RetiredPractice => Number >= 29;
    public bool StorageReferences => StorageReferenceRemovalSchema.Present(Number);
    public bool UnifiedSourceTrigger => Number is >= 24 and < 26;
}
