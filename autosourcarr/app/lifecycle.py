"""Ordered cleanup that owns every stop operation, including cancellation."""
import asyncio
import inspect


async def cleanup_steps(steps, *, primary=None, report=None):
    errors=[]
    async def run():
        for name,stop in steps:
            try:
                result=stop()
                if inspect.isawaitable(result): await result
            except BaseException as exc:
                errors.append(exc)
                if report:
                    try: report(name,exc)
                    except Exception: pass
    task=asyncio.create_task(run())
    cancelled=False
    while not task.done():
        try: await asyncio.shield(task)
        except asyncio.CancelledError: cancelled=True
    task.result()
    if primary is not None: return
    if cancelled: raise asyncio.CancelledError()
    if errors: raise errors[0]
