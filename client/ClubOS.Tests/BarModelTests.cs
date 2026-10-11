using ClubOS.Shared;
using ClubOS.Shared.Ui;
using Microsoft.Extensions.Time.Testing;

namespace ClubOS.Tests;

public class BarModelTests
{
    private static AgentSnapshot Cabinet(int balance = 500) =>
        new(true, PcMode.Locked, new AccountInfo(2, "guest", "+7 *** ***-00-01", balance), null, null, null);

    private static readonly IReadOnlyList<ProductInfo> Menu = new List<ProductInfo>
    {
        new(1, "Кола", "Напитки", 80),
        new(2, "Чай", "Напитки", 60),
        new(3, "Бургер", "Еда", 250),
    };

    private sealed class Rig
    {
        public FakeAgent Agent = new();
        public FakeTimeProvider Time = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        public FlowController Flow = null!;
        public BarModel Bar = null!;
        public int Balance = 500;
        public List<OrderInfo> Orders = [];
        public CallInfo? OpenCall;
        public IReadOnlyList<ProductInfo> Products = Menu;
        public Func<IpcRequest, IpcResponse?>? Override;

        public Rig()
        {
            Agent.Handler = r =>
            {
                var custom = Override?.Invoke(r);
                if (custom is not null) return custom;
                return r.Op switch
                {
                    Ops.Tariffs => Agent.Ok(r, tariffs: []),
                    Ops.Products => Ok(r, products: Products),
                    Ops.Orders => Ok(r, orders: Orders),
                    Ops.OrderCreate => Create(r),
                    Ops.OrderCancel => Cancel(r),
                    Ops.CallAdmin => Call(r),
                    Ops.CallState => Ok(r, call: OpenCall),
                    _ => Ok(r),
                };
            };
            Flow = new FlowController(Agent, Time);
            Bar = new BarModel(Flow, Agent, Time);
        }

        public AgentSnapshot Snap => Cabinet(Balance);

        public IpcResponse Ok(IpcRequest r, IReadOnlyList<ProductInfo>? products = null, IReadOnlyList<OrderInfo>? orders = null, CallInfo? call = null) =>
            new(r.Id, true, 200, null, Snap, null, null, null, null, products, orders, call);

        private IpcResponse Create(IpcRequest r)
        {
            var total = r.Items!.Sum(i => Products.First(p => p.Id == i.ProductId).Price * i.Qty);
            if (Balance < total) return IpcResponse.Fail(r.Id, 402, $"Не хватает {total - Balance} ₽ на балансе", Snap);
            Balance -= total;
            Orders.Insert(0, new OrderInfo(Orders.Count + 1, total, "new", null, Time.GetUtcNow().UtcDateTime,
                r.Items!.Select(i => new OrderLine(Products.First(p => p.Id == i.ProductId).Name, 0, i.Qty)).ToList()));
            return Ok(r);
        }

        private IpcResponse Cancel(IpcRequest r)
        {
            var o = Orders.FirstOrDefault(x => x.Id == r.OrderId);
            if (o is null) return IpcResponse.Fail(r.Id, 404, "Заказ не найден", Snap);
            if (o.Status != "new") return IpcResponse.Fail(r.Id, 409, "Заказ уже принят в работу", Snap);
            Balance += o.Total;
            Orders[Orders.IndexOf(o)] = o with { Status = "cancelled" };
            return Ok(r);
        }

        private IpcResponse Call(IpcRequest r)
        {
            OpenCall = new CallInfo(1, "open", null);
            return Ok(r, call: OpenCall);
        }

        public async Task EnterAsync()
        {
            Agent.Current = Snap;
            await Flow.PollAsync();
            await Bar.PollAsync(true);
        }

        public async Task<(IReadOnlyList<ProductCard> Cards, IReadOnlyList<CategoryChip> Chips)> OpenBarAsync()
        {
            await EnterAsync();
            return Bar.Build("", "");
        }
    }

    // ---------- меню ----------
    [Fact]
    public async Task Menu_is_loaded_when_the_bar_is_opened_and_grouped_by_category_chips()
    {
        var rig = new Rig();
        var (cards, chips) = await rig.OpenBarAsync();
        Assert.Equal(["Кола", "Чай", "Бургер"], cards.Select(c => c.Name));
        Assert.Equal(["Все", "Напитки", "Еда"], chips.Select(c => c.Title));
        Assert.Equal("80 ₽", cards[0].PriceText);
        Assert.True(rig.Bar.ProductsLoaded);
    }

    [Fact]
    public async Task Menu_is_not_requested_while_the_guest_is_not_on_the_bar_tab()
    {
        var rig = new Rig();
        rig.Agent.Current = rig.Snap;
        await rig.Flow.PollAsync();
        await rig.Bar.PollAsync(false);
        Assert.Empty(rig.Agent.Ops(Ops.Products));
    }

    [Fact]
    public async Task Search_and_category_filter_the_menu()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        Assert.Equal(["Бургер"], rig.Bar.Build("", "Еда").Cards.Select(c => c.Name));
        Assert.Equal(["Чай"], rig.Bar.Build("  ЧАЙ ", "").Cards.Select(c => c.Name));
        Assert.Empty(rig.Bar.Build("пицца", "").Cards);
        Assert.Equal(3, rig.Bar.Build("", "несуществующая").Cards.Count);   // неизвестная категория сбрасывается
    }

    [Fact]
    public async Task Failed_menu_load_is_flagged_retried_soon_and_cleared_on_success()
    {
        var rig = new Rig();
        rig.Override = r => r.Op == Ops.Products ? IpcResponse.Fail(r.Id, 401, "Вход недействителен", rig.Snap) : null;
        await rig.EnterAsync();
        Assert.False(rig.Bar.ProductsLoaded);
        Assert.True(rig.Bar.ProductsFailed);
        var tries = rig.Agent.Ops(Ops.Products).Count();

        await rig.Bar.PollAsync(true);                                      // сразу заново не долбим
        Assert.Equal(tries, rig.Agent.Ops(Ops.Products).Count());
        rig.Override = null;
        rig.Time.Advance(TimeSpan.FromSeconds(16));
        await rig.Bar.PollAsync(true);
        Assert.True(rig.Bar.ProductsLoaded);
        Assert.False(rig.Bar.ProductsFailed);
    }

    // ---------- корзина ----------
    [Fact]
    public async Task Cart_adds_removes_and_totals()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(1);
        rig.Bar.Add(1);
        rig.Bar.Add(3);
        Assert.Equal(2 * 80 + 250, rig.Bar.Total);
        Assert.Equal("410 ₽", rig.Bar.TotalText);
        Assert.Equal([("Кола", 2, "160 ₽"), ("Бургер", 1, "250 ₽")], rig.Bar.Cart.Select(c => (c.Name, c.Qty, c.SumText)));
        Assert.Equal(2, rig.Bar.Build("", "").Cards[0].Qty);                // на карточке видно, сколько в корзине

        rig.Bar.Remove(1);
        rig.Bar.Remove(3);
        Assert.Equal(80, rig.Bar.Total);
        rig.Bar.Remove(1);
        Assert.False(rig.Bar.HasCart);
        rig.Bar.Remove(1);                                                  // лишнее нажатие ничего не ломает
        Assert.Equal(0, rig.Bar.Total);
    }

    [Fact]
    public async Task Cart_ignores_unknown_products_and_caps_the_quantity()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(999);
        Assert.False(rig.Bar.HasCart);
        for (var i = 0; i < 25; i++) rig.Bar.Add(2);
        Assert.Equal(BarModel.MaxQtyPerProduct, rig.Bar.Cart.Single().Qty);
        Assert.Contains("Не больше 20", rig.Bar.Error);
    }

    [Fact]
    public async Task Order_button_needs_enough_money_and_says_how_much_is_missing()
    {
        var rig = new Rig { Balance = 100 };
        await rig.OpenBarAsync();
        rig.Bar.Add(3);
        Assert.False(rig.Bar.CanOrder);
        Assert.Equal("Не хватает 150 ₽ на балансе", rig.Bar.CartHint);
        rig.Bar.Clear();
        rig.Bar.Add(2);
        Assert.True(rig.Bar.CanOrder);
        Assert.Null(rig.Bar.CartHint);
    }

    // ---------- заказ ----------
    [Fact]
    public async Task Placing_an_order_sends_the_cart_clears_it_and_shows_the_order_in_the_list()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(1);
        rig.Bar.Add(1);
        rig.Bar.Add(3);
        await rig.Bar.PlaceOrderAsync();

        var sent = rig.Agent.Ops(Ops.OrderCreate).Single();
        Assert.Equal([new CartLine(1, 2), new CartLine(3, 1)], sent.Items);
        Assert.False(rig.Bar.HasCart);
        Assert.Contains("Заказ принят", rig.Bar.Note);
        Assert.Null(rig.Bar.Error);
        Assert.Equal(90, rig.Flow.Balance);                                 // баланс на экране обновился сразу
        var order = rig.Bar.Orders.Single();
        Assert.Equal("Ждёт принятия", order.StatusText);
        Assert.Equal("Кола x 2, Бургер", order.Summary);
        Assert.True(order.CanCancel && order.Open);
    }

    [Fact]
    public async Task Server_refusal_keeps_the_cart_and_shows_the_reason()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(3);
        rig.Override = r => r.Op == Ops.OrderCreate ? IpcResponse.Fail(r.Id, 429, "У вас уже несколько заказов в работе", rig.Snap) : null;
        await rig.Bar.PlaceOrderAsync();
        Assert.True(rig.Bar.HasCart);
        Assert.Equal("У вас уже несколько заказов в работе", rig.Bar.Error);
    }

    [Fact]
    public async Task A_dish_removed_from_the_menu_triggers_a_menu_refresh_and_leaves_the_cart()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(3);
        rig.Override = r => r.Op == Ops.OrderCreate ? IpcResponse.Fail(r.Id, 404, "Одного из товаров уже нет в меню", rig.Snap) : null;
        await rig.Bar.PlaceOrderAsync();
        rig.Override = null;
        rig.Products = Menu.Where(p => p.Id != 3).ToList();
        await rig.Bar.PollAsync(true);                                      // меню перезагрузилось
        Assert.False(rig.Bar.HasCart, "убранный из меню товар не должен висеть в корзине");
        Assert.Equal(2, rig.Bar.Build("", "").Cards.Count);
    }

    [Fact]
    public async Task Double_click_on_order_does_not_send_it_twice()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(1);
        var first = rig.Bar.PlaceOrderAsync();
        var second = rig.Bar.PlaceOrderAsync();
        await Task.WhenAll(first, second);
        Assert.Single(rig.Agent.Ops(Ops.OrderCreate));
    }

    [Fact]
    public async Task Guest_can_cancel_a_new_order_and_gets_the_money_back()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(3);
        await rig.Bar.PlaceOrderAsync();
        Assert.Equal(250, rig.Flow.Balance);
        await rig.Bar.CancelOrderAsync(rig.Bar.Orders.Single().Id);
        Assert.Equal(500, rig.Flow.Balance);
        Assert.Contains("деньги вернулись", rig.Bar.Note);
        Assert.Equal("Отменён", rig.Bar.Orders.Single().StatusText);
        Assert.False(rig.Bar.Orders.Single().CanCancel);
    }

    [Fact]
    public async Task Accepted_order_cannot_be_cancelled_by_the_guest_and_progress_is_polled()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(3);
        await rig.Bar.PlaceOrderAsync();
        rig.Orders[0] = rig.Orders[0] with { Status = "accepted" };         // сотрудник принял
        rig.Time.Advance(TimeSpan.FromSeconds(9));
        await rig.Bar.PollAsync(false);                                     // вкладка «Бар» закрыта, но заказ в работе: статус обновляется
        Assert.Equal("Готовится", rig.Bar.Orders.Single().StatusText);
        Assert.False(rig.Bar.Orders.Single().CanCancel);

        await rig.Bar.CancelOrderAsync(1);
        Assert.Equal("Заказ уже принят в работу", rig.Bar.Error);
    }

    // ---------- вызов администратора ----------
    [Fact]
    public async Task Calling_the_admin_marks_the_call_open_and_ignores_repeat_presses()
    {
        var rig = new Rig();
        await rig.EnterAsync();
        Assert.Equal("Вызвать администратора", rig.Bar.CallButtonText);
        await rig.Bar.CallAdminAsync();
        Assert.True(rig.Bar.CallOpen);
        Assert.Equal("Администратор вызван", rig.Bar.CallButtonText);
        Assert.Contains("Администратор вызван", rig.Bar.Note);
        await rig.Bar.CallAdminAsync();
        Assert.Single(rig.Agent.Ops(Ops.CallAdmin));
    }

    [Fact]
    public async Task The_call_state_is_cleared_when_the_admin_comes()
    {
        var rig = new Rig();
        await rig.EnterAsync();
        await rig.Bar.CallAdminAsync();
        rig.OpenCall = null;                                                // сотрудник нажал «Подошёл»
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        await rig.Bar.PollAsync(false);
        Assert.False(rig.Bar.CallOpen);
        Assert.Equal("Администратор подошёл", rig.Bar.Note);
    }

    [Fact]
    public async Task An_already_open_call_is_noticed_after_the_overlay_restarts()
    {
        var rig = new Rig { OpenCall = new CallInfo(7, "open", null) };
        await rig.EnterAsync();
        Assert.True(rig.Bar.CallOpen);
    }

    [Fact]
    public async Task Failed_call_shows_the_error_and_can_be_retried()
    {
        var rig = new Rig();
        await rig.EnterAsync();
        rig.Override = r => r.Op == Ops.CallAdmin ? IpcResponse.Fail(r.Id, 0, "Нет связи с сервером", rig.Snap) : null;
        await rig.Bar.CallAdminAsync();
        Assert.False(rig.Bar.CallOpen);
        Assert.Equal("Нет связи с сервером", rig.Bar.Error);
        rig.Override = null;
        await rig.Bar.CallAdminAsync();
        Assert.True(rig.Bar.CallOpen);
    }

    // ---------- конец сессии гостя ----------
    [Fact]
    public async Task Reset_forgets_the_cart_the_orders_and_the_call_for_the_next_guest()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(1);
        await rig.Bar.CallAdminAsync();
        rig.Bar.Reset();
        Assert.False(rig.Bar.HasCart);
        Assert.Empty(rig.Bar.Orders);
        Assert.False(rig.Bar.CallOpen);
        Assert.Null(rig.Bar.Note);
    }

    [Fact]
    public async Task Messages_disappear_by_themselves()
    {
        var rig = new Rig();
        await rig.OpenBarAsync();
        rig.Bar.Add(3);
        await rig.Bar.PlaceOrderAsync();
        Assert.NotNull(rig.Bar.Note);
        rig.Time.Advance(TimeSpan.FromSeconds(13));
        await rig.Bar.PollAsync(false);
        Assert.Null(rig.Bar.Note);
    }
}
