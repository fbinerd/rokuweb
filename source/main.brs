sub main()
    screen = CreateObject("roSGScreen")
    port = CreateObject("roMessagePort")
    screen.SetMessagePort(port)

    scene = screen.CreateScene("HomeScene")
    screen.Show()

    while true
        msg = wait(0, port)
        if type(msg) = "roSGScreenEvent" and msg.IsScreenClosed()
            return
        end if
    end while
end sub
