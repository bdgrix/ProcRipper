<h3>
  <img src="https://github.com/JaKooLit/Telegram-Animated-Emojis/blob/main/Activity/Sparkles.webp" alt="Sparkles" width="38" height="38" />
   ProcRipper
  <img src="https://github.com/JaKooLit/Telegram-Animated-Emojis/blob/main/Activity/Sparkles.webp" alt="Sparkles" width="38" height="38" />
</h3>
Showcase


<div align="center">

https://github.com/user-attachments/assets/d7efae69-3301-4b01-9fa0-4fa84991f97b

</div>

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
  - per-app memory limit

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

1. Run `ProcRipper.exe` as administrator.
2. Configure your target process and threads in the `.GCFG` files.
3. Launch the target game; thread settings will be applied automatically.
4. you can copy ProcRipper.exe shortcut into shell:startup so its run on startup (optional)
 
## Logging

- Threads not found in the config are logged to the console.
- appylied Thread optimizion will display in console
- game proc detection

### Donation 


I Put So Much Of Time Into ProcRipper And At least I Want You Gauys Support

how ? there is 2 way

First: Donate With Crypto

| Coin       | Address                                      |
|------------|----------------------------------------------|
| USDT (BEP20) | `0xC3cE92Ce2663b2Eb32216A4A604F5e158B8e2c68` |
| BTC        | `bc1qmvrpm49fagv2v2wpdz2svu5drk4hny2ydj9s7w` |
| ETH        | `0xC3cE92Ce2663b2Eb32216A4A604F5e158B8e2c68` |
| SOL        | `E3CVLRFFGun4FhBbc3kJ4aihWhTbqXtt5eRYccKJYoSN` |
| LTC        | `ltc1qzydnfefx30lk7sp7wcacejkdr9nf2sxyhwzaks` |
| BNB        | `0xC3cE92Ce2663b2Eb32216A4A604F5e158B8e2c68` |
| USDT (ERC20) | `0xC3cE92Ce2663b2Eb32216A4A604F5e158B8e2c68` |
| USDT (TRC20) | `TGC3TYRF3ebv6M5iqz4JSZpELpq19QeACT` |

## Discord
https://discord.gg/pVuejEbn
