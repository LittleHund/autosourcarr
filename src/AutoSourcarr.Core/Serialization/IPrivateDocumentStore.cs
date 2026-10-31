namespace AutoSourcarr.Core.Serialization;

public interface IPrivateDocument
{
    byte[]? Read(int maximumBytes);
    void Write(byte[] bytes);
}

/// <summary>Synchronous, physically owned transactions over private JSON documents.</summary>
public interface IPrivateDocumentStore
{
    T Locked<T>(string name, Func<IPrivateDocument, T> operation, TimeSpan? timeout = null);
    IReadOnlyList<string> JsonDocuments(int maximum);
}
