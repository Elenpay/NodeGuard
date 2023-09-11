namespace NodeGuard.Data.Repositories.Interfaces;

public interface ISaltRepository
{
    (bool, string) Salt();
    // For future implementations
    // bool StoreSalt(string salt, string token);
}