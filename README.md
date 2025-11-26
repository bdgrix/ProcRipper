# ThreadOptimizer

Optimizes thread priority, affinity, and boost for games and other applications to maximize performance on Windows.

## Features

- Per-thread configuration for games via `GAME_PRIORITY.GCFG`.
- Configurable settings for any process using `PROC_PRIORITY.GCFG`.
- Automatic detection of target game processes.
- Applies settings after threads are fully initialized (up to 2 minutes wait).
- Exact thread name matching for precise control.
- Runs with elevated permissions to manage priorities and affinities.
- Future planned features:
  - Automatic power plan switching
  - Automatic profile generation
  - Module optimizion
  - more thread optimizion by default

## Configuration

### GAME_PRIORITY.GCFG

```
# THREAD_NAME=PRIORITY,AFFINITY,DISABLE_BOOST
# Priorities: 15=TIME_CRITICAL, 2=HIGHEST, 1=ABOVE_NORMAL, 0=NORMAL, -1=BELOW_NORMAL, -2=LOWEST, -15=IDLE
# Affinity: ALL, 0,2,4,6, 0-3, 0,1
# DisableBoost: true/false
```

### PROC_PRIORITY.GCFG

- Used for non-game applications.
- Same format as `GAME_PRIORITY.GCFG`.

> PROC_PRIORITY will run when game proc detect

## Usage

1. Run `ThreadOptimizer.exe` as administrator.
2. Configure your target process and threads in the `.GCFG` files.
3. Launch the target game; thread settings will be applied automatically.

## Logging

- Threads not found in the config are logged to the console.
- appylied Thread optimizion will display in console
- game proc detection

## License

[Attribution-Style License] – allows usage, modification, and closed-source redistribution with attribution.
