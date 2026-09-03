namespace NotyWin.App.Models;

public interface INotePersistence
{
    /// <summary>Load every note. Called once at startup to seed the in-memory list.</summary>
    IReadOnlyList<Note> LoadAll();
    void Upsert(Note note);
    void Delete(string id);
}