import asyncio, sys, os
from playwright.async_api import async_playwright
BASE="http://127.0.0.1:5119"; OUT="/tmp/ev/resp"
PUB=["/","/products","/about","/contact","/services","/faq","/gallery","/privacy","/terms","/Account/Login","/Account/Register","/Account/ForgotPassword","/cart","/portal/orders/track"]
PORTAL=["/portal","/portal/shop","/portal/orders","/portal/account","/portal/profile","/portal/profile/password","/portal/addresses","/portal/invoices","/portal/payments","/portal/notifications","/portal/wishlist","/this-page-does-not-exist","/checkout"]
W=[320,375,390,430,768,1024,1440]
SNAP={320:1,430:1,768:1,1024:1}
async def main():
    bad=0; total=0; lines=[]
    async with async_playwright() as p:
        b=await p.chromium.launch(); ctx=await b.new_context(); pg=await ctx.new_page()
        # login
        await pg.goto(BASE+"/Account/Login"); await pg.fill('input[name="Email"]',"dr-final-buyer@example.com"); await pg.fill('input[name="Password"]',"CustE2E!2026xY"); await pg.click('button[type="submit"]'); await pg.wait_for_load_state("networkidle")
        # find a direct product and add to cart
        await pg.goto(BASE+"/portal/shop?search=direct"); await pg.wait_for_load_state("networkidle")
        card=await pg.query_selector('[data-product-id]'); pid=await card.get_attribute('data-product-id') if card else None
        prod=f"/portal/shop/{pid}" if pid else "/portal/shop"
        if pid:
            await pg.goto(BASE+prod); btn=await pg.query_selector('text=Add to cart')
            if btn: await btn.click(); await pg.wait_for_load_state("networkidle")
        ws=None
        await pg.goto(BASE+"/portal/orders"); import re
        hs=await pg.eval_on_selector_all('a[href*="/portal/orders/"]','els=>els.map(e=>e.getAttribute("href"))')
        hs=[h for h in hs if re.search(r"/portal/orders/[0-9a-f-]{36}$",h)]
        ws=sorted(hs)[0] if hs else None
        pub_prod=f"/products/{pid}" if pid else "/products"
        await pg.goto(BASE+"/portal/shop?search=custom"); c2=await pg.query_selector('[data-product-id]'); cid=await c2.get_attribute('data-product-id') if c2 else None
        extra=[pub_prod]+([f"/portal/shop/{cid}",f"/portal/shop/{cid}/order"] if cid else [])
        pages=PUB+PORTAL+[prod]+extra+([ws] if ws else [])
        names={"/":"home","/portal/shop":"shop",prod:"product","/checkout":"checkout",(ws or "x"):"workspace"}
        for w in W:
            await pg.set_viewport_size({"width":w,"height":844})
            for u in pages:
                await pg.goto(BASE+u); await pg.wait_for_load_state("networkidle")
                sw=await pg.evaluate("Math.max(document.documentElement.scrollWidth,document.body.scrollWidth)")
                total+=1; ok=sw<=w
                if not ok: bad+=1
                lines.append(f"{w:5d} {u:40s} scrollWidth={sw} {'OK' if ok else 'OVERFLOW'}")
                if w in SNAP and u in names: await pg.screenshot(path=f"{OUT}/w{w}-{names[u]}.png",full_page=True)
        await b.close()
    lines.append(f"TOTAL={total} OVERFLOW={bad}")
    open(f"{OUT}/overflow-scan.txt","w").write("\n".join(lines)+"\n"); print(lines[-1])
asyncio.run(main())
