namespace GithubActionsOrchestrator;

public static class ListExtensionMethods
{
    public static T PopAt<T>(this List<T> list, int index)
    {
        T r = list[index];
        list.RemoveAt(index);
        return r;
    }

    public static T PopFirst<T>(this List<T> list, Predicate<T> predicate)
    {
        int index = list.FindIndex(predicate);
        T r = list[index];
        list.RemoveAt(index);
        return r;
    }

    public static T PopFirstOrDefault<T>(this List<T> list, Predicate<T> predicate) where T : class
    {
        int index = list.FindIndex(predicate);
        if (index > -1)
        {
            T r = list[index];
            list.RemoveAt(index);
            return r;
        }
        return null;
    }
}