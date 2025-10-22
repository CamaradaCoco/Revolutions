using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Demo.Pages
{
    public class MapModel : PageModel
    {
        public void OnGet()
        {
            // No server-side data required; map fetches /api/revolutions client-side.
        }
    }
}
