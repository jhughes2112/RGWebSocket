#nullable enable
﻿using System;

namespace Logging
{
    public enum EVerbosity
    {
        Error    = 0,
        Warning  = 1,
        Info     = 2,
        Debug    = 3,
        Extreme  = 4,
    }

    public interface ILogging : IDisposable
    {
        EVerbosity Verbosity { get; set; }
        void Log(EVerbosity level, string msg);
    }
}
