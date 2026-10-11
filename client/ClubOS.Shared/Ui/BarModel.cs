using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ClubOS.Shared.Ui;

/// <summary>Товар в меню бара, готовый к показу. Qty: сколько уже в корзине.</summary>
public sealed record ProductCard(int Id, string Name, string Category, string PriceText, int Qty)
{
    public bool InCart => Qty > 0;
}

/// <summary>Строка корзины.</summary>
public sealed record CartItem(int Id, string Name, int Qty, string SumText);

/// <summary>Заказ гостя в списке «Мои заказы». Open: ещё не выдан и не отменён.</summary>
public sealed record OrderView(int Id, string Title, string StatusText, string Summary, bool CanCancel, bool Open);

/// <summary>
/// Бар и вызов администратора: меню, корзина, заказ с оплатой с баланса, мои заказы, кнопка «Вызвать администратора».
/// Не знает про WPF. Цены и сумма считаются только для показа: сервер пересчитывает их сам и не верит клиенту.
/// </summary>
public sealed class BarModel : INotifyPropertyChanged
{
    public const int MaxQtyPerProduct = 20;
    private static readonly TimeSpan ProductsRefreshEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan OrdersRefreshEvery = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CallRefreshEvery = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(12);
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly FlowController _flow;
    private readonly IAgentClient _client;
    private readonly TimeProvider _time;
    private int _nextId;

    private IReadOnlyList<ProductInfo> _products = [];
    private bool _productsLoaded, _needProducts = true, _needOrders = true, _needCallCheck = true, _busy;
    private DateTimeOffset _productsAt = DateTimeOffset.MinValue, _ordersAt = DateTimeOffset.MinValue,
        _callAt = DateTimeOffset.MinValue, _messageAt;
    private readonly SortedDictionary<int, int> _cart = [];
    private IReadOnlyList<CartItem> _cartItems = [];
    private IReadOnlyList<OrderView> _orders = [];
    private string? _error, _note;
    private bool _callOpen;

    public BarModel(FlowController flow, IAgentClient client, TimeProvider? time = null)
    {
        _flow = flow;
        _client = client;
        _time = time ?? TimeProvider.System;
        flow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FlowController.Balance)) RaiseMoney();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Изменилось то, что видно в списке товаров (меню или корзина): окно меню пересобирает карточки.</summary>
    public event Action? Changed;

    // ---------- что показывать ----------
    public IReadOnlyList<CartItem> Cart { get => _cartItems; private set => Set(ref _cartItems, value); }
    public IReadOnlyList<OrderView> Orders { get => _orders; private set { if (Set(ref _orders, value)) OnChanged(nameof(HasOrders)); } }
    public bool HasOrders => _orders.Count > 0;
    public bool HasCart => _cart.Count > 0;
    public bool ProductsLoaded => _productsLoaded;
    /// <summary>Последняя попытка загрузить меню не удалась (и старого меню нет).</summary>
    public bool ProductsFailed { get; private set; }
    public bool Busy { get => _busy; private set { if (Set(ref _busy, value)) { OnChanged(nameof(CanOrder)); OnChanged(nameof(CanCall)); } } }
    public string? Error { get => _error; private set => Set(ref _error, value); }
    public string? Note { get => _note; private set => Set(ref _note, value); }
    public bool CallOpen { get => _callOpen; private set { if (Set(ref _callOpen, value)) { OnChanged(nameof(CallButtonText)); OnChanged(nameof(CallShortText)); OnChanged(nameof(CanCall)); } } }
    public bool CanCall => !CallOpen && !Busy;
    public string CallButtonText => CallOpen ? "Администратор вызван" : "Вызвать администратора";
    /// <summary>Короткая подпись плитки в правой колонке (полное название лежит в подсказке).</summary>
    public string CallShortText => CallOpen ? "Админ вызван" : "Вызвать админа";

    public int Total => _cart.Sum(p => (_products.FirstOrDefault(x => x.Id == p.Key)?.Price ?? 0) * p.Value);
    public string TotalText => FormatMoney(Total);
    public bool CanOrder => HasCart && !Busy && _flow.Balance >= Total;
    /// <summary>Подсказка под кнопкой «Заказать»: чего не хватает. Null, если всё в порядке или корзина пуста.</summary>
    public string? CartHint => HasCart && _flow.Balance < Total ? $"Не хватает {Total - _flow.Balance} ₽ на балансе" : null;

    // ---------- меню ----------
    /// <summary>Карточки и категории для текущего поиска и выбранной категории.</summary>
    public (IReadOnlyList<ProductCard> Cards, IReadOnlyList<CategoryChip> Chips) Build(string search, string category)
    {
        var categories = _products.Select(p => p.Category).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        if (category.Length > 0 && !categories.Contains(category, StringComparer.CurrentCultureIgnoreCase)) category = "";
        List<CategoryChip> chips = categories.Count > 1
            ? new[] { new CategoryChip("", "Все", category.Length == 0) }
                .Concat(categories.Select(c => new CategoryChip(c, c, string.Equals(c, category, StringComparison.CurrentCultureIgnoreCase)))).ToList()
            : [];
        var needle = search.Trim();
        var cards = _products
            .Where(p => category.Length == 0 || string.Equals(p.Category, category, StringComparison.CurrentCultureIgnoreCase))
            .Where(p => needle.Length == 0 || Ru.CompareInfo.IndexOf(p.Name, needle, CompareOptions.IgnoreCase) >= 0)
            .Select(p => new ProductCard(p.Id, p.Name, p.Category, FormatMoney(p.Price), _cart.GetValueOrDefault(p.Id)))
            .ToList();
        return (cards, chips);
    }

    // ---------- корзина ----------
    public void Add(int productId)
    {
        if (_products.All(p => p.Id != productId)) return;
        var qty = _cart.GetValueOrDefault(productId);
        if (qty >= MaxQtyPerProduct) { Say(error: $"Не больше {MaxQtyPerProduct} штук одного товара"); return; }
        _cart[productId] = qty + 1;
        CartChanged();
    }

    public void Remove(int productId)
    {
        if (!_cart.TryGetValue(productId, out var qty)) return;
        if (qty <= 1) _cart.Remove(productId); else _cart[productId] = qty - 1;
        CartChanged();
    }

    public void Clear()
    {
        if (_cart.Count == 0) return;
        _cart.Clear();
        CartChanged();
    }

    private void CartChanged()
    {
        Error = null;
        Cart = _cart.Select(p =>
        {
            var product = _products.First(x => x.Id == p.Key);
            return new CartItem(product.Id, product.Name, p.Value, FormatMoney(product.Price * p.Value));
        }).ToList();
        RaiseMoney();
        Changed?.Invoke();
    }

    private void RaiseMoney()
    {
        foreach (var name in new[] { nameof(Total), nameof(TotalText), nameof(HasCart), nameof(CanOrder), nameof(CartHint) }) OnChanged(name);
    }

    // ---------- действия ----------
    public async Task PlaceOrderAsync(CancellationToken ct = default)
    {
        if (!CanOrder) return;
        Busy = true;
        Error = Note = null;
        try
        {
            var lines = _cart.Select(p => new CartLine(p.Key, p.Value)).ToList();
            var r = await CallAsync(new(0, Ops.OrderCreate, Items: lines), ct);
            if (!r.Ok)
            {
                Say(error: r.Error ?? "Не удалось отправить заказ");
                if (r.Status == 404) _needProducts = true;   // товар убрали из меню: обновим список
                return;
            }
            _cart.Clear();
            CartChanged();
            Say(note: "Заказ принят. Принесём к вашему месту, оплата списана с баланса");
            _needOrders = true;
            await _flow.PollAsync(ct);                       // баланс на экране обновится сразу
            await LoadOrdersAsync(ct);
        }
        finally { Busy = false; }
    }

    public async Task CancelOrderAsync(int orderId, CancellationToken ct = default)
    {
        if (Busy) return;
        Busy = true;
        Error = Note = null;
        try
        {
            var r = await CallAsync(new(0, Ops.OrderCancel, OrderId: orderId), ct);
            if (!r.Ok) Say(error: r.Error ?? "Не удалось отменить заказ");
            else
            {
                Say(note: "Заказ отменён, деньги вернулись на баланс");
                await _flow.PollAsync(ct);
            }
            await LoadOrdersAsync(ct);
        }
        finally { Busy = false; }
    }

    public Task CallAdminAsync(CancellationToken ct = default) => CallAsync(null, "Администратор вызван, скоро подойдёт", ct);

    /// <summary>
    /// «Пополнить баланс»: пока онлайн-оплаты нет, пополняет администратор. Кнопка вызывает его с понятной записью, чтобы он сразу знал, зачем подходит.
    /// </summary>
    public Task TopUpAsync(CancellationToken ct = default) => CallAsync("Пополнение баланса", "Администратор скоро подойдёт, чтобы пополнить баланс", ct);

    private async Task CallAsync(string? text, string note, CancellationToken ct)
    {
        if (CallOpen || Busy) return;
        Busy = true;
        try
        {
            var r = await CallAsync(new(0, Ops.CallAdmin, Text: text), ct);
            if (!r.Ok) { Say(error: r.Error ?? "Не удалось вызвать администратора"); return; }
            CallOpen = true;
            _callAt = _time.GetUtcNow();
            Say(note: note);
        }
        finally { Busy = false; }
    }

    /// <summary>Раз в секунду из LauncherModel. onBar: гость сейчас на вкладке «Бар».</summary>
    public async Task PollAsync(bool onBar, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        if ((Error is not null || Note is not null) && now - _messageAt >= MessageLifetime) Error = Note = null;

        if (onBar && (_needProducts || now - _productsAt >= ProductsRefreshEvery)) await LoadProductsAsync(now, ct);
        var hasOpenOrders = _orders.Any(o => o.Open);
        if ((onBar && (_needOrders || now - _ordersAt >= OrdersRefreshEvery)) || (hasOpenOrders && now - _ordersAt >= OrdersRefreshEvery))
            await LoadOrdersAsync(ct);
        if ((CallOpen || _needCallCheck) && now - _callAt >= CallRefreshEvery) await LoadCallAsync(now, ct);
    }

    /// <summary>Гость вышел из меню (кабинет закрылся): корзина, сообщения и заказы не должны достаться следующему гостю.</summary>
    public void Reset()
    {
        _cart.Clear();
        Cart = [];
        Orders = [];
        Error = Note = null;
        CallOpen = false;
        _needOrders = _needCallCheck = true;
        _needProducts = true;
        RaiseMoney();
        Changed?.Invoke();
    }

    // ---------- внутреннее ----------
    private async Task LoadProductsAsync(DateTimeOffset now, CancellationToken ct)
    {
        _needProducts = false;
        _productsAt = now;
        var r = await CallAsync(new(0, Ops.Products), ct);
        if (r.Ok && r.Products is not null)
        {
            _products = r.Products;
            _productsLoaded = true;
            ProductsFailed = false;
            // убрали из меню то, что лежит в корзине: корзину чистим от таких строк
            foreach (var gone in _cart.Keys.Where(id => _products.All(p => p.Id != id)).ToList()) _cart.Remove(gone);
            CartChanged();
        }
        else
        {
            ProductsFailed = !_productsLoaded;
            _productsAt = now - ProductsRefreshEvery + TimeSpan.FromSeconds(15);   // повторим скоро, старое меню остаётся
        }
        Changed?.Invoke();
    }

    private async Task LoadOrdersAsync(CancellationToken ct)
    {
        _needOrders = false;
        _ordersAt = _time.GetUtcNow();
        var r = await CallAsync(new(0, Ops.Orders), ct);
        if (r.Ok && r.Orders is not null)
            Orders = r.Orders.Where(o => o.Status != "cancelled" || _time.GetUtcNow() - new DateTimeOffset(DateTime.SpecifyKind(o.CreatedAt, DateTimeKind.Utc)) < TimeSpan.FromHours(1))
                .Take(5).Select(ToView).ToList();
    }

    private async Task LoadCallAsync(DateTimeOffset now, CancellationToken ct)
    {
        _needCallCheck = false;
        _callAt = now;
        var r = await CallAsync(new(0, Ops.CallState), ct);
        if (r.Ok)
        {
            var wasOpen = CallOpen;
            CallOpen = r.OpenCall is not null;
            if (wasOpen && !CallOpen) Say(note: "Администратор подошёл");
        }
    }

    private static OrderView ToView(OrderInfo o)
    {
        var status = o.Status switch { "new" => "Ждёт принятия", "accepted" => "Готовится", "done" => "Выдан", _ => "Отменён" };
        var summary = string.Join(", ", o.Items.Select(i => i.Qty > 1 ? $"{i.Name} x {i.Qty}" : i.Name));
        return new(o.Id, $"Заказ №{o.Id}, {FormatMoney(o.Total)}", status, summary, o.Status == "new", o.Status is "new" or "accepted");
    }

    private Task<IpcResponse> CallAsync(IpcRequest request, CancellationToken ct) =>
        _client.SendAsync(request with { Id = Interlocked.Increment(ref _nextId) }, ct);

    private void Say(string? error = null, string? note = null)
    {
        _messageAt = _time.GetUtcNow();
        if (error is not null) { Note = null; Error = error; }
        else { Error = null; Note = note; }
    }

    private static string FormatMoney(int v) => v.ToString("N0", Ru) + " ₽";

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnChanged(name);
        return true;
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
