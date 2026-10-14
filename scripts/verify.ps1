param(
    [string]$ApiUrl = 'http://localhost:5080',
    [string]$WebUrl = 'http://localhost:3000'
)
$ErrorActionPreference = 'Stop'
$checks = 0
function Assert-That([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:checks++
    Write-Host "PASS: $Message"
}
function Assert-Status([string]$Url, [int]$Expected, [string]$Method = 'GET', [hashtable]$Headers = @{}) {
    try { $status = [int](Invoke-WebRequest -Uri $Url -Method $Method -Headers $Headers -UseBasicParsing).StatusCode }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        $status = [int]$_.Exception.Response.StatusCode
    }
    Assert-That ($status -eq $Expected) "HTTP $Expected for $Url"
}

Assert-Status "$ApiUrl/health/live" 200
Assert-Status "$ApiUrl/health/ready" 200
Assert-Status $WebUrl 200
$catalog = Invoke-RestMethod "$ApiUrl/api/products?pageSize=48"
Assert-That ($catalog.total -eq 32 -and $catalog.items.Count -eq 32) '32 seeded products are available'
Assert-That (($catalog.items | Select-Object -ExpandProperty id -Unique).Count -eq 32) 'Product identifiers are unique'
$categories = Invoke-RestMethod "$ApiUrl/api/categories"
Assert-That ($categories.Count -eq 4 -and ($categories | Measure-Object count -Sum).Sum -eq 32) 'Category counts match the catalog'
$audio = Invoke-RestMethod "$ApiUrl/api/products?category=Audio&maxPrice=100&sort=price-asc"
Assert-That ($audio.total -eq 4) 'Combined category and price filters return the expected products'
Assert-That (@($audio.items | Where-Object { $_.category -ne 'Audio' -or $_.price -gt 100 }).Count -eq 0) 'Filtered results respect all constraints'
Assert-That ($audio.items[0].price -eq 49 -and $audio.items[1].price -eq 69 -and $audio.items[2].price -eq 89 -and $audio.items[3].price -eq 99) 'Ascending prices are ordered correctly'
$descending = Invoke-RestMethod "$ApiUrl/api/products?sort=price-desc&pageSize=48"
Assert-That ($descending.items[0].price -eq 249 -and $descending.items[-1].price -eq 39) 'Descending prices are ordered correctly'
$search = Invoke-RestMethod "$ApiUrl/api/products?search=FORMA"
Assert-That ($search.total -eq 1 -and $search.items[0].name -eq 'Forma Wireless') 'Search is case-insensitive'
$wildcard = Invoke-RestMethod "$ApiUrl/api/products?search=%25"
Assert-That ($wildcard.total -eq 1 -and $wildcard.items[0].name -eq 'Tempo Mechanical') 'Percent characters are literal, not search wildcards'
$inStock = Invoke-RestMethod "$ApiUrl/api/products?inStock=true&pageSize=48"
Assert-That ($inStock.total -eq @($catalog.items | Where-Object stock -gt 0).Count -and @($inStock.items | Where-Object stock -le 0).Count -eq 0) 'In-stock filtering reflects current inventory'
$first = Invoke-RestMethod "$ApiUrl/api/products?page=1"
$second = Invoke-RestMethod "$ApiUrl/api/products?page=2"
Assert-That ($first.items.Count -eq 12 -and $second.items.Count -eq 12) 'Pagination returns the configured page size'
Assert-That (@($second.items | Where-Object { $_.id -in $first.items.id }).Count -eq 0) 'Adjacent pages do not overlap'
$detail = Invoke-RestMethod "$ApiUrl/api/products/$($first.items[0].id)"
Assert-That ($detail.name -eq $first.items[0].name -and $detail.price -eq $first.items[0].price) 'Product details agree with the catalog'
$missing = Invoke-RestMethod "$ApiUrl/api/products?search=no-such-product-7392"
Assert-That ($missing.total -eq 0 -and $missing.items.Count -eq 0) 'Unknown searches return a genuine empty result'
$proxy = Invoke-RestMethod "$WebUrl/api/catalog/products?category=Audio&maxPrice=100"
Assert-That ($proxy.total -eq $audio.total) 'The same-origin web gateway preserves filtering'
Assert-Status "$ApiUrl/api/products?page=0" 400
Assert-Status "$ApiUrl/api/products?pageSize=999" 400
Assert-Status "$ApiUrl/api/products?minPrice=200&maxPrice=10" 400
Assert-Status "$ApiUrl/api/products?category=Unknown" 400
Assert-Status "$ApiUrl/api/products?sort=unknown" 400
Assert-Status "$ApiUrl/api/products/00000000-0000-0000-0000-000000000000" 404
Assert-Status "$WebUrl/api/catalog/health" 404
Assert-Status "$WebUrl/api/shop/cart" 401
Assert-Status "$WebUrl/api/shop/auth/logout" 403 'POST' @{ Origin = 'https://example.com' }
Assert-Status "$WebUrl/api/shop/auth/logout" 400 'POST' @{ Origin = 'http://localhost:3000' }
Assert-Status "$WebUrl/api/shop/unknown" 404
Assert-Status "$WebUrl/api/shop/orders" 401
Assert-Status "$WebUrl/api/shop/actions" 401
Assert-Status "$WebUrl/api/shop/actions/00000000-0000-0000-0000-000000000000/confirm" 403 'POST' @{ Origin = 'https://example.com' }
Assert-Status "$WebUrl/api/shop/actions/00000000-0000-0000-0000-000000000000/confirm" 400 'POST' @{ Origin = 'http://localhost:3000' }
Assert-Status "$WebUrl/api/shop/actions/00000000-0000-0000-0000-000000000000/confirm" 405
Assert-Status "$WebUrl/api/shop/orders/00000000-0000-0000-0000-000000000000/advance" 404 'POST' @{ Origin = 'http://localhost:3000' }
Write-Host "Verified $checks checks against the running application."
