sub init()
    m.top.functionName = "execute"
end sub

sub execute()
    socket = CreateObject("roDatagramSocket")
    if socket = invalid
        m.top.errorMessage = "roDatagramSocket indisponivel."
        m.top.completedToken = "error-socket"
        return
    end if

    datagram = GetInterface(socket, "ifDatagramSocket")
    if datagram = invalid
        m.top.errorMessage = "ifDatagramSocket indisponivel."
        m.top.completedToken = "error-interface"
        return
    end if

    ok = datagram.bindToLocalPort(0)
    if not ok
        reason = ""
        reason = getString(datagram.getFailureReason(), "")
        if reason = ""
            reason = "Falha ao bindar porta UDP local."
        end if
        m.top.errorMessage = reason
        m.top.completedToken = "error-bind"
        return
    end if

    m.top.receiverPort = datagram.getLocalPort()
    m.top.completedToken = "ready-" + m.top.receiverPort.ToStr()

    while true
        sleep(1000)
    end while
end sub

function getString(value as dynamic, fallback as string) as string
    if value = invalid
        return fallback
    end if

    valueType = Type(value)
    if valueType = "roString" or valueType = "String"
        return value
    end if

    return fallback
end function
