namespace Quickstart;

public class UserRepository
{
    private readonly Dictionary<Guid, User> _Users = new Dictionary<Guid, User>();

    public void Store(User User)
    {
        _Users[User.Id] = User;
    }

    public User Get(Guid id)
    {
        if (_Users.TryGetValue(id, out var User))
        {
            return User;
        }

        throw new ArgumentOutOfRangeException(nameof(id), "User does not exist");
    }
}