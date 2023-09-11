using NodeGuard.Data.Repositories.Interfaces;

namespace NodeGuard.Data.Repositories;

public class SaltRepository: ISaltRepository
{
    public (bool, string) Salt()
    {
        return (true, Constants.API_TOKEN_SALT);
    }
}