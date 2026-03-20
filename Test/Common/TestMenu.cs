namespace Test.Common;

public class TestMenu
{
    private readonly List<MenuItem> _items = new();
    private readonly string _title;

    public TestMenu(string title)
    {
        _title = title;
    }

    public TestMenu Add(string key, string description, Func<Task> action)
    {
        _items.Add(new MenuItem(key, description, action));
        return this;
    }

    public TestMenu Add(string key, string description, Action action)
    {
        _items.Add(new MenuItem(key, description, () => { action(); return Task.CompletedTask; }));
        return this;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            Console.WriteLine($"\n=== {_title} ===\n");

            foreach (var item in _items)
                Console.WriteLine($"{item.Key}. {item.Description}");

            Console.Write("\n请选择: ");
            var input = Console.ReadLine();

            var selected = _items.FirstOrDefault(i => i.Key == input);
            if (selected != null)
            {
                try
                {
                    await selected.Action();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"错误: {ex.Message}");
                }
            }
            else
            {
                Console.WriteLine("无效选择");
            }
        }
    }

    private record MenuItem(string Key, string Description, Func<Task> Action);
}