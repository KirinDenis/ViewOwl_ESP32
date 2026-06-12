using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ViewOwl.Grabber.WebAPI.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class GrabberController : ControllerBase
    {
        private readonly ILogger<GrabberController> _logger;

        
        public GrabberController(ILogger<GrabberController> logger)
        {
            _logger = logger;
        }

        [HttpGet]
        public bool Get()
        {
            return true;
        }
    }
}
